using Erp.Sdk;

namespace Norway.Saft;

/// <summary>The export cannot be made as asked (missing company details, an account without standard account, ...); the message is for the user.</summary>
public sealed class SaftExportException(string message) : Exception(message);

/// <summary>
/// Collects what a SAF-T Financial file needs from the ledger through the data API: accounts with opening and closing
/// balances, customers and suppliers, the VAT codes, and the entries of the period with VAT information on the base lines.
/// </summary>
public static class SaftExporter
{
    private const int PageSize = 1000;

    public static async Task<SaftData> LoadAsync(Erp.Sdk.AppContext ctx, DateOnly from, DateOnly to)
    {
        var settings = (await ctx.Data.QueryAsync("norway.settings", top: 1)).FirstOrDefault();
        var missing = new List<string>();
        void Require(string field, string label)
        {
            if (string.IsNullOrWhiteSpace(settings?.Get<string>(field)))
            {
                missing.Add(label);
            }
        }

        Require("companyName", "firmanavn");
        Require("organizationNumber", "organisasjonsnummer");
        Require("contactFirstName", "kontaktperson (fornavn)");
        Require("contactLastName", "kontaktperson (etternavn)");
        if (missing.Count > 0)
        {
            throw new SaftExportException($"Fyll ut {string.Join(", ", missing)} i «Norsk oppsett» før du eksporterer.");
        }

        var organization = settings!.Get<string>("organizationNumber")!.Trim();
        var company = new SaftCompany(
            settings.Get<string>("companyName")!.Trim(),
            organization,
            settings.Get<bool>("vatRegistered") ? $"{organization}MVA" : null,
            Clean(settings.Get<string>("street")),
            Clean(settings.Get<string>("postalCode")),
            Clean(settings.Get<string>("city")),
            settings.Get<string>("contactFirstName")!.Trim(),
            settings.Get<string>("contactLastName")!.Trim(),
            Clean(settings.Get<string>("telephone")),
            Clean(settings.Get<string>("email")));
        var currency = (await ctx.Data.QueryAsync("finance.settings", top: 1)).FirstOrDefault()?.Get<string>("baseCurrency") ?? "NOK";

        var accounts = (await QueryAllAsync(ctx, "finance.account", null)).ToDictionary(a => a.Id);
        var postings = await QueryAllAsync(ctx, "finance.posting", $"postingDate le '{to:yyyy-MM-dd}'", "created_at");
        var yearStart = (await ctx.Data.QueryAsync("core.fiscalyear", $"startDate le '{from:yyyy-MM-dd}' and endDate ge '{from:yyyy-MM-dd}'", top: 1))
            .FirstOrDefault()?.Get<DateOnly>("startDate") ?? new DateOnly(from.Year, 1, 1);

        // Balances: balance sheet accounts carry everything before the period; result accounts only the year so far
        decimal Net(Record p) => p.Get<decimal>("debit") - p.Get<decimal>("credit");
        bool IsResult(Record account) => account.Get<string>("accountType") is "revenue" or "expense";
        bool BeforePeriod(Record p, Record account) =>
            p.Get<DateOnly>("postingDate") < from && (!IsResult(account) || p.Get<DateOnly>("postingDate") >= yearStart);

        var saftAccounts = new List<SaftAccount>();
        var unmapped = new List<string>();
        foreach (var group in postings.GroupBy(p => p.Get<Guid>("account")))
        {
            if (!accounts.TryGetValue(group.Key, out var account))
            {
                continue;
            }

            var category = Clean(account.Get<string>("norway.groupingCategory"));
            var code = Clean(account.Get<string>("norway.groupingCode"));
            if (category is null || code is null)
            {
                unmapped.Add(account.Get<string>("number")!);
                continue;
            }

            var opening = group.Where(p => BeforePeriod(p, account)).Sum(Net);
            var movement = group.Where(p => p.Get<DateOnly>("postingDate") >= from).Sum(Net);
            saftAccounts.Add(new SaftAccount(account.Get<string>("number")!, account.Get<string>("name")!, category, code, opening, opening + movement));
        }

        if (unmapped.Count > 0)
        {
            throw new SaftExportException(
                $"Kontoer uten SAF-T grupperingskategori og -kode: {string.Join(", ", unmapped.Order().Take(15))}. Sett dem på kontokortet.");
        }

        // VAT codes and rates
        var codes = (await QueryAllAsync(ctx, "norway.vatcode", null)).ToDictionary(c => c.Id);
        var taxCodes = new List<SaftTaxCode>();
        foreach (var rate in await QueryAllAsync(ctx, "norway.vatrate", null))
        {
            if (codes.TryGetValue(rate.Get<Guid>("vatCode"), out var code))
            {
                taxCodes.Add(new SaftTaxCode(
                    code.Get<string>("code")!, code.Get<string>("name")!, rate.Get<decimal>("rate"),
                    rate.Get<DateOnly?>("validFrom"), rate.Get<DateOnly?>("validTo")));
            }
        }

        decimal PercentageOn(string code, DateOnly date) =>
            taxCodes.Where(t => t.Code == code && (t.EffectiveDate is null || t.EffectiveDate <= date) && (t.ExpirationDate is null || t.ExpirationDate >= date))
                .OrderByDescending(t => t.EffectiveDate).Select(t => t.Percentage).FirstOrDefault();

        // Customers and suppliers: parties on control accounts
        var parties = new Dictionary<Guid, Record?>();
        async Task<Record?> PartyAsync(Guid id)
        {
            if (!parties.TryGetValue(id, out var party))
            {
                parties[id] = party = await ctx.Data.GetAsync("core.party", id);
            }

            return party;
        }

        var customers = new List<SaftParty>();
        var suppliers = new List<SaftParty>();
        var controlled = postings.Where(p => p.Get<Guid?>("party") is not null && accounts.TryGetValue(p.Get<Guid>("account"), out var a) && a.Get<string>("controlType") != "none");
        foreach (var group in controlled.GroupBy(p => (Party: p.Get<Guid>("party"), Account: p.Get<Guid>("account"))))
        {
            var account = accounts[group.Key.Account];
            var party = await PartyAsync(group.Key.Party);
            if (party is null)
            {
                continue;
            }

            var opening = group.Where(p => BeforePeriod(p, account)).Sum(Net);
            var closing = group.Sum(Net);
            var entry = new SaftParty(
                party.Get<string>("partyNumber") ?? party.Id.ToString("N"),
                party.Get<string>("name") ?? "-",
                Clean(party.Get<string>("organizationNumber")),
                account.Get<string>("number")!,
                opening,
                closing);
            (account.Get<string>("controlType") == "receivable" ? customers : suppliers).Add(entry);
        }

        // Entries of the period
        var entries = (await QueryAllAsync(ctx, "finance.journalentry", $"entryDate ge '{from:yyyy-MM-dd}' and entryDate le '{to:yyyy-MM-dd}'"))
            .OrderBy(e => e.Get<string>("entryNumber"), StringComparer.Ordinal).ToList();
        var vatEntryIds = (await QueryAllAsync(ctx, "norway.vatentry", $"entryDate ge '{from:yyyy-MM-dd}' and entryDate le '{to:yyyy-MM-dd}'"))
            .Select(v => v.Get<Guid>("entry")).ToHashSet();

        var byEntry = postings.Where(p => p.Get<DateOnly>("postingDate") >= from).GroupBy(p => p.Get<Guid>("entry")).ToDictionary(g => g.Key, g => g.ToList());
        var transactions = new List<SaftTransaction>();
        foreach (var entry in entries)
        {
            if (!byEntry.TryGetValue(entry.Id, out var entryPostings))
            {
                continue;
            }

            var date = entry.Get<DateOnly>("entryDate");
            var text = entry.Get<string>("description") is { Length: > 0 } d ? d : "-";
            var tax = vatEntryIds.Contains(entry.Id) ? await TaxOfEntryAsync(ctx, entry, entries, accounts, codes, PercentageOn) : [];
            var lines = new List<SaftLine>();
            foreach (var posting in entryPostings)
            {
                var account = accounts[posting.Get<Guid>("account")];
                var debit = posting.Get<decimal>("debit");
                var credit = posting.Get<decimal>("credit");
                var match = tax.FirstOrDefault(t => t.Account == account.Id && t.Debit == debit && t.Credit == credit);
                if (match is not null)
                {
                    tax.Remove(match);
                }

                var partyId = posting.Get<Guid?>("party") is { } pid && account.Get<string>("controlType") != "none" ? await PartyAsync(pid) : null;
                var partyKey = partyId is null ? null : partyId.Get<string>("partyNumber") ?? partyId.Id.ToString("N");
                lines.Add(new SaftLine(
                    (lines.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    account.Get<string>("number")!,
                    posting.Get<string>("description") is { Length: > 0 } pd ? pd : text,
                    debit,
                    credit,
                    account.Get<string>("controlType") == "receivable" ? partyKey : null,
                    account.Get<string>("controlType") == "payable" ? partyKey : null,
                    null,
                    posting.Get<string>("currency"),
                    posting.Get<decimal?>("currencyAmount"),
                    posting.Get<decimal?>("exchangeRate"),
                    match?.Tax));
            }

            var postingDate = entryPostings[0].Get<DateOnly>("postingDate");
            transactions.Add(new SaftTransaction(
                entry.Get<string>("entryNumber")!, postingDate.Month, postingDate.Year, postingDate, text, date, lines));
        }

        return new SaftData(company, currency, ctx.Today, from, to, saftAccounts.OrderBy(a => a.Id, StringComparer.Ordinal).ToList(),
            customers, suppliers, taxCodes, transactions);
    }

    private sealed class TaxedLine(Guid account, decimal debit, decimal credit, SaftTax tax)
    {
        public Guid Account { get; } = account;
        public decimal Debit { get; } = debit;
        public decimal Credit { get; } = credit;
        public SaftTax Tax { get; } = tax;
    }

    /// <summary>
    /// The base lines of an entry that carry a VAT code, with their VAT amounts. For a reversing entry these are the original
    /// entry's lines on the opposite sides.
    /// </summary>
    private static async Task<List<TaxedLine>> TaxOfEntryAsync(
        Erp.Sdk.AppContext ctx,
        Record entry,
        IReadOnlyList<Record> entriesInPeriod,
        Dictionary<Guid, Record> accounts,
        Dictionary<Guid, Record> codes,
        Func<string, DateOnly, decimal> percentageOn)
    {
        var reversed = entry.Get<Guid?>("reversalOf");
        var source = reversed is { } id
            ? entriesInPeriod.FirstOrDefault(e => e.Id == id) ?? await ctx.Data.GetAsync("finance.journalentry", id)
            : entry;
        if (source is null)
        {
            return [];
        }

        var date = source.Get<DateOnly>("entryDate");
        var lines = await ctx.Data.QueryAsync("finance.voucherline", $"voucher eq '{source.Get<Guid>("voucher")}'", top: PageSize);
        var result = new List<TaxedLine>();
        foreach (var line in lines.Where(l => l.Get<Guid?>("norway.vatCode") is not null && l.Get<Guid?>("norway.vatSourceLine") is null))
        {
            if (!codes.TryGetValue(line.Get<Guid>("norway.vatCode"), out var code))
            {
                continue;
            }

            var vat = lines.Where(l => l.Get<Guid?>("norway.vatSourceLine") == line.Id).Sum(l => l.Get<decimal>("debit") + l.Get<decimal>("credit"));
            var debit = line.Get<decimal>("debit");
            var credit = line.Get<decimal>("credit");
            var tax = new SaftTax(code.Get<string>("code")!, percentageOn(code.Get<string>("code")!, date), Math.Max(debit, credit), vat);
            result.Add(reversed is null
                ? new TaxedLine(line.Get<Guid>("account"), debit, credit, tax)
                : new TaxedLine(line.Get<Guid>("account"), credit, debit, tax));
        }

        return result;
    }

    private static async Task<List<Record>> QueryAllAsync(Erp.Sdk.AppContext ctx, string objectId, string? filter, string? orderBy = "created_at")
    {
        var all = new List<Record>();
        while (true)
        {
            var page = await ctx.Data.QueryAsync(objectId, filter, orderBy, PageSize, all.Count);
            all.AddRange(page);
            if (page.Count < PageSize)
            {
                return all;
            }
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
