using System.Globalization;
using System.Reflection;
using Erp.Sdk;
using Norway.Saft;

namespace Norway;

public sealed record FiscalYearCreated(Guid FiscalyearId);

public sealed record SetupRequested(Guid SettingsId);

public sealed record JournalPosted(Guid EntryId, Guid VoucherId);

public sealed record SaftExportRequested(Guid ExportId);

/// <summary>
/// Everything Norwegian in the accounting: the standard chart of accounts (NS 4102) with SAF-T standard accounts, the VAT
/// codes with rates and validity, VAT on voucher lines, the VAT return per term, and the rule that a voucher has a text. The
/// general ledger (<c>finance</c>) does not know this app; it is attached through extension fields on voucher lines and
/// accounts, hooks on the voucher lines, and the event <c>finance.journal.posted@1</c>.
/// </summary>
public sealed class NorwayApp : AppBase
{
    /// <summary>Bump when the chart or the VAT codes shipped with the app change; the set-up then runs again (adds what is missing).</summary>
    public const string ChartVersion = "2026.2";

    private const string Settings = "norway.settings";
    private const string VatCode = "norway.vatcode";
    private const string VatRate = "norway.vatrate";
    private const string VatEntry = "norway.vatentry";
    private const string VatReturn = "norway.vatreturn";
    private const string VatReturnLine = "norway.vatreturnline";
    private const string SaftExport = "norway.saftexport";
    private const string KidObject = "norway.kid";
    private const string OpenItem = "finance.openitem";
    private const string Payment = "finance.payment";
    private const string Account = "finance.account";
    private const string Voucher = "finance.voucher";
    private const string Line = "finance.voucherline";
    private const string Entry = "finance.journalentry";

    private const string LineVatCode = "norway.vatCode";
    private const string LineVatSource = "norway.vatSourceLine";
    private const string AccountCategory = "norway.groupingCategory";
    private const string AccountCode = "norway.groupingCode";

    // ------------------------------------------------------------------ set-up: chart of accounts and VAT codes

    [OnEvent("core.fiscalyear.created@1")]
    public Task OnFiscalYearCreated(FiscalYearCreated e, Erp.Sdk.AppContext ctx) => EnsureSetupAsync(ctx);

    [OnEvent("norway.setup.requested@1")]
    public Task OnSetupRequested(SetupRequested e, Erp.Sdk.AppContext ctx) => EnsureSetupAsync(ctx);

    /// <summary>Creating the settings record without a version asks for the set-up (it can take longer than a hook may, so it is an event).</summary>
    [OnAfterSave(Settings)]
    public async Task AfterSettingsSaved(Record settings, Erp.Sdk.AppContext ctx)
    {
        if (settings.Get<string>("chartVersion") != ChartVersion)
        {
            await ctx.Events.PublishAsync("norway.setup.requested@1", settings.Id.ToString(), new { settingsId = settings.Id });
        }
    }

    /// <summary>
    /// Adds the chart of accounts and VAT codes the company does not have yet (idempotent; delivery is at least once and
    /// what a company has changed or deleted is not touched once the version is recorded).
    /// </summary>
    private static async Task EnsureSetupAsync(Erp.Sdk.AppContext ctx)
    {
        var settings = (await ctx.Data.QueryAsync(Settings, top: 1)).FirstOrDefault();
        if (settings?.Get<string>("chartVersion") == ChartVersion)
        {
            return;
        }

        var accounts = (await ctx.Data.QueryAsync(Account, top: 5000)).ToDictionary(a => a.Get<string>("number")!, a => a.Id);
        foreach (var row in ReadCsv("chart-of-accounts.csv"))
        {
            if (accounts.ContainsKey(row["number"]))
            {
                continue;
            }

            var account = await ctx.Data.CreateAsync(Account, new Dictionary<string, object?>
            {
                ["number"] = row["number"],
                ["name"] = row["name"],
                ["accountType"] = row["type"],
                ["controlType"] = row["control"],
                [AccountCategory] = row["groupingCategory"],
                [AccountCode] = row["groupingCode"]
            });
            accounts[row["number"]] = account.Id;
        }

        var codes = (await ctx.Data.QueryAsync(VatCode, top: 500)).Select(c => c.Get<string>("code")!).ToHashSet();
        foreach (var row in ReadCsv("vat-codes.csv"))
        {
            if (codes.Contains(row["code"]))
            {
                continue;
            }

            var code = await ctx.Data.CreateAsync(VatCode, new Dictionary<string, object?>
            {
                ["code"] = row["code"],
                ["name"] = row["name"],
                ["direction"] = row["direction"],
                ["account"] = row["account"].Length > 0 && accounts.TryGetValue(row["account"], out var accountId) ? accountId : null
            });
            await ctx.Data.CreateAsync(VatRate, new Dictionary<string, object?>
            {
                ["vatCode"] = code.Id,
                ["rate"] = decimal.Parse(row["rate"], CultureInfo.InvariantCulture),
                ["validFrom"] = DateOnly.ParseExact(row["validFrom"], "yyyy-MM-dd", CultureInfo.InvariantCulture)
            });
        }

        // Read again: creating records may have changed the row version.
        settings = (await ctx.Data.QueryAsync(Settings, top: 1)).FirstOrDefault();
        if (settings is null)
        {
            await ctx.Data.CreateAsync(Settings, new Dictionary<string, object?> { ["chartVersion"] = ChartVersion });
        }
        else
        {
            await ctx.Data.UpdateAsync(Settings, settings.Id, settings.RowVersion, new Dictionary<string, object?> { ["chartVersion"] = ChartVersion });
        }
    }

    private static List<Dictionary<string, string>> ReadCsv(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Norway.Data.{name}")
            ?? throw new InvalidOperationException($"Data file '{name}' is missing from the app.");
        using var reader = new StreamReader(stream);
        var header = reader.ReadLine()!.Split(';');
        var rows = new List<Dictionary<string, string>>();
        while (reader.ReadLine() is { Length: > 0 } text)
        {
            var cells = text.Split(';');
            rows.Add(header.Select((h, i) => (h, cell: i < cells.Length ? cells[i] : string.Empty)).ToDictionary(x => x.h, x => x.cell));
        }

        return rows;
    }

    // ------------------------------------------------------------------ VAT on voucher lines

    /// <summary>A line with a VAT code needs an active code with a rate on the voucher date.</summary>
    [OnValidate(Line)]
    public async Task<ValidationResult> ValidateLine(Record line, Erp.Sdk.AppContext ctx)
    {
        if (line.Get<Guid?>(LineVatCode) is not { } codeId)
        {
            return ValidationResult.Ok;
        }

        if (line.Get<Guid?>(LineVatSource) is not null)
        {
            return ValidationResult.Error(LineVatCode, "Linjen er laget av appen for MVA-beløpet og kan ikke selv ha MVA-kode.");
        }

        var voucher = line.Get<Guid?>("voucher") is { } voucherId ? await ctx.Data.GetAsync(Voucher, voucherId) : null;
        if (voucher?.Get<DateOnly?>("voucherDate") is not { } date)
        {
            return ValidationResult.Ok;
        }

        return await ResolveAsync(ctx, codeId, date) is { Error: { } error }
            ? ValidationResult.Error(LineVatCode, error)
            : ValidationResult.Ok;
    }

    /// <summary>
    /// Keeps the VAT line of a line with a VAT code in step: it is created, changed or removed so that it always matches the
    /// base line (net amount × the rate on the voucher date, on the VAT account of the code, on the same side).
    /// </summary>
    [OnAfterSave(Line)]
    public async Task AfterLineSaved(Record line, Erp.Sdk.AppContext ctx)
    {
        if (line.Get<string>("status") == "posted" || line.Get<Guid?>(LineVatSource) is not null)
        {
            return;
        }

        // A deleted line arrives with no fields: its VAT line goes with it.
        if (line.Get<Guid?>("voucher") is not { } voucherId)
        {
            foreach (var orphan in await ctx.Data.QueryAsync(Line, $"{LineVatSource} eq '{line.Id}'", top: 10))
            {
                await ctx.Data.DeleteAsync(Line, orphan.Id, orphan.RowVersion);
            }

            return;
        }

        var lines = await ctx.Data.QueryAsync(Line, $"voucher eq '{voucherId}'", top: 1000);
        var ids = lines.Select(l => l.Id).ToHashSet();

        // A VAT line whose base line is gone is removed
        foreach (var orphan in lines.Where(l => l.Get<Guid?>(LineVatSource) is { } source && !ids.Contains(source)))
        {
            await ctx.Data.DeleteAsync(Line, orphan.Id, orphan.RowVersion);
        }

        var generated = lines.FirstOrDefault(l => l.Get<Guid?>(LineVatSource) == line.Id);
        var voucher = await ctx.Data.GetAsync(Voucher, voucherId);
        var date = voucher?.Get<DateOnly?>("voucherDate");
        var expected = line.Get<Guid?>(LineVatCode) is { } codeId && date is { } d
            ? await ExpectedVatLineAsync(ctx, line, codeId, d)
            : null;

        if (expected is null)
        {
            if (generated is not null)
            {
                await ctx.Data.DeleteAsync(Line, generated.Id, generated.RowVersion);
            }

            return;
        }

        var fields = new Dictionary<string, object?>
        {
            ["voucher"] = voucherId,
            ["account"] = expected.Account,
            ["description"] = expected.Text,
            ["debit"] = expected.Side == "debit" ? expected.Amount : 0m,
            ["credit"] = expected.Side == "credit" ? expected.Amount : 0m,
            [LineVatSource] = line.Id
        };
        if (generated is null)
        {
            await ctx.Data.CreateAsync(Line, fields);
        }
        else if (generated.Get<Guid>("account") != expected.Account
                 || generated.Get<decimal>("debit") != (decimal)fields["debit"]!
                 || generated.Get<decimal>("credit") != (decimal)fields["credit"]!)
        {
            await ctx.Data.UpdateAsync(Line, generated.Id, generated.RowVersion, fields);
        }
    }

    private sealed record ExpectedVat(Guid Account, string Side, decimal Amount, string Text);

    /// <summary>The VAT line a base line should have, or null when the rate is 0 (the line only carries a basis).</summary>
    private static async Task<ExpectedVat?> ExpectedVatLineAsync(Erp.Sdk.AppContext ctx, Record line, Guid codeId, DateOnly date)
    {
        var resolved = await ResolveAsync(ctx, codeId, date);
        if (resolved.Error is not null || resolved.Rate is not { } rate || resolved.Code is not { } code)
        {
            return null;
        }

        var debit = line.Get<decimal>("debit");
        var credit = line.Get<decimal>("credit");
        var net = debit > 0 ? debit : credit;
        var amount = Math.Round(net * rate / 100m, 2, MidpointRounding.AwayFromZero);
        if (amount == 0 || code.Get<Guid?>("account") is not { } account)
        {
            return null;
        }

        return new ExpectedVat(account, debit > 0 ? "debit" : "credit", amount, $"MVA {code.Get<string>("code")}");
    }

    private sealed record Resolved(Record? Code, decimal? Rate, string? Error);

    /// <summary>Finds the VAT code and its rate on <paramref name="date"/> (the rate with the latest start that covers the date).</summary>
    private static async Task<Resolved> ResolveAsync(Erp.Sdk.AppContext ctx, Guid codeId, DateOnly date)
    {
        var code = await ctx.Data.GetAsync(VatCode, codeId);
        if (code is null || !code.Get<bool>("isActive"))
        {
            return new Resolved(code, null, "MVA-koden finnes ikke eller er ikke aktiv.");
        }

        var rate = (await ctx.Data.QueryAsync(VatRate, $"vatCode eq '{codeId}'", top: 200))
            .Where(r => r.Get<DateOnly>("validFrom") <= date && (r.Get<DateOnly?>("validTo") is not { } to || to >= date))
            .OrderByDescending(r => r.Get<DateOnly>("validFrom"))
            .FirstOrDefault();
        if (rate is null)
        {
            return new Resolved(code, null, $"MVA-kode {code.Get<string>("code")} har ingen sats den {date:yyyy-MM-dd}.");
        }

        // The account may have been removed from the chart after the code was set up (the reference crosses apps, so the
        // database does not hold it): the code then has to be pointed at an account again.
        if (rate.Get<decimal>("rate") > 0
            && (code.Get<Guid?>("account") is not { } accountId || await ctx.Data.GetAsync(Account, accountId) is null))
        {
            return new Resolved(code, null, $"MVA-kode {code.Get<string>("code")} mangler MVA-konto (kontoen er ikke satt eller er fjernet).");
        }

        return new Resolved(code, rate.Get<decimal>("rate"), null);
    }

    // ------------------------------------------------------------------ Norwegian rules when posting

    /// <summary>
    /// At posting: the voucher has a text, and every VAT line matches its base line (so a hand-edited or stale VAT line cannot
    /// be posted) and every VAT line has a base line.
    /// </summary>
    [OnValidate(Voucher)]
    public async Task<ValidationResult> ValidateVoucher(Record voucher, Erp.Sdk.AppContext ctx)
    {
        if (voucher.Get<string>("status") != "posted")
        {
            return ValidationResult.Ok;
        }

        var failures = new List<ValidationFailure>();
        if (string.IsNullOrWhiteSpace(voucher.Get<string>("description")))
        {
            failures.Add(new ValidationFailure("description", "Bilaget må ha en tekst som beskriver transaksjonen."));
        }

        if (voucher.Get<DateOnly?>("voucherDate") is { } date)
        {
            var lines = await ctx.Data.QueryAsync(Line, $"voucher eq '{voucher.Id}'", top: 1000);
            var ids = lines.Select(l => l.Id).ToHashSet();
            foreach (var orphan in lines.Where(l => l.Get<Guid?>(LineVatSource) is { } source && !ids.Contains(source)))
            {
                failures.Add(new ValidationFailure("lines", "En MVA-linje mangler linjen med grunnlaget. Fjern MVA-linjen."));
            }

            foreach (var source in lines.Where(l => l.Get<Guid?>(LineVatCode) is not null && l.Get<Guid?>(LineVatSource) is null))
            {
                var codeId = source.Get<Guid>(LineVatCode);
                var resolved = await ResolveAsync(ctx, codeId, date);
                if (resolved.Error is not null)
                {
                    failures.Add(new ValidationFailure("lines", resolved.Error));
                    continue;
                }

                var expected = await ExpectedVatLineAsync(ctx, source, codeId, date);
                var found = lines.Where(l => l.Get<Guid?>(LineVatSource) == source.Id).ToList();
                var matches = expected is null
                    ? found.Count == 0
                    : found.Count == 1
                      && found[0].Get<Guid>("account") == expected.Account
                      && found[0].Get<decimal>(expected.Side) == expected.Amount
                      && found[0].Get<decimal>(expected.Side == "debit" ? "credit" : "debit") == 0;
                if (!matches)
                {
                    failures.Add(new ValidationFailure(
                        "lines",
                        $"MVA-linjen for MVA-kode {resolved.Code!.Get<string>("code")} stemmer ikke med grunnlaget. Lagre grunnlagslinjen på nytt."));
                }
            }
        }

        return failures.Count == 0 ? ValidationResult.Ok : ValidationResult.Errors(failures.DistinctBy(f => f.Message));
    }

    // ------------------------------------------------------------------ VAT entries (basis and VAT per code)

    /// <summary>
    /// When an entry is posted, records basis and VAT per base line with a VAT code, so the VAT return can add them up by
    /// code. A reversing entry records the opposite of the entry it reverses. Safe to run again (the source key is unique).
    /// </summary>
    [OnEvent("finance.journal.posted@1")]
    public async Task OnJournalPosted(JournalPosted e, Erp.Sdk.AppContext ctx)
    {
        var entry = await ctx.Data.GetAsync(Entry, e.EntryId);
        if (entry is null || (await ctx.Data.QueryAsync(VatEntry, $"entry eq '{e.EntryId}'", top: 1)).Count > 0)
        {
            return;
        }

        var date = entry.Get<DateOnly>("entryDate");
        if (entry.Get<Guid?>("reversalOf") is { } reversed)
        {
            foreach (var original in await ctx.Data.QueryAsync(VatEntry, $"entry eq '{reversed}'", top: 1000))
            {
                await CreateVatEntryAsync(ctx, $"rev:{original.Id}", e.EntryId, date, original.Get<Guid>("vatCode"),
                    -original.Get<decimal>("basis"), -original.Get<decimal>("vatAmount"));
            }

            return;
        }

        var lines = await ctx.Data.QueryAsync(Line, $"voucher eq '{e.VoucherId}'", top: 1000);
        foreach (var source in lines.Where(l => l.Get<Guid?>(LineVatCode) is not null && l.Get<Guid?>(LineVatSource) is null))
        {
            var codeId = source.Get<Guid>(LineVatCode);
            var code = await ctx.Data.GetAsync(VatCode, codeId);
            if (code is null)
            {
                continue;
            }

            // Output codes count credit as positive, input codes count debit as positive.
            decimal Signed(Record l) => code.Get<string>("direction") == "output"
                ? l.Get<decimal>("credit") - l.Get<decimal>("debit")
                : l.Get<decimal>("debit") - l.Get<decimal>("credit");
            var vat = lines.Where(l => l.Get<Guid?>(LineVatSource) == source.Id).Sum(Signed);
            await CreateVatEntryAsync(ctx, source.Id.ToString(), e.EntryId, date, codeId, Signed(source), vat);
        }
    }

    private static Task CreateVatEntryAsync(Erp.Sdk.AppContext ctx, string sourceKey, Guid entryId, DateOnly date, Guid codeId, decimal basis, decimal vat) =>
        ctx.Data.CreateAsync(VatEntry, new Dictionary<string, object?>
        {
            ["sourceKey"] = sourceKey,
            ["entry"] = entryId,
            ["entryDate"] = date,
            ["vatCode"] = codeId,
            ["basis"] = basis,
            ["vatAmount"] = vat
        });

    // ------------------------------------------------------------------ KID

    /// <summary>
    /// A new customer item (an invoice, a positive amount on a receivable account) gets a KID as its payment reference, so the
    /// payer's bank payment can be matched to it. The number is a gap-free series with a MOD10 check digit.
    /// </summary>
    [OnAfterSave(OpenItem)]
    public async Task AfterOpenItemSaved(Record saved, Erp.Sdk.AppContext ctx)
    {
        // An update carries only what changed, so the item is read in full
        var item = await ctx.Data.GetAsync(OpenItem, saved.Id);
        if (item is null || !string.IsNullOrEmpty(item.Get<string>("paymentReference")) || item.Get<decimal>("amount") <= 0
            || await ctx.Data.GetAsync(Account, item.Get<Guid>("account")) is not { } account
            || account.Get<string>("controlType") != "receivable")
        {
            return;
        }

        var existing = (await ctx.Data.QueryAsync(KidObject, $"openItem eq '{item.Id}'", top: 1)).FirstOrDefault();
        var row = existing ?? await ctx.Data.CreateAsync(KidObject, new Dictionary<string, object?> { ["openItem"] = item.Id });
        var kid = row.Get<string>("kid");
        if (string.IsNullOrEmpty(kid))
        {
            kid = Kid.WithMod10(row.Get<string>("number")!);
            await ctx.Data.UpdateAsync(KidObject, row.Id, row.RowVersion, new Dictionary<string, object?> { ["kid"] = kid });
        }

        var current = (await ctx.Data.GetAsync(OpenItem, item.Id))!;
        await ctx.Data.UpdateAsync(OpenItem, current.Id, current.RowVersion, new Dictionary<string, object?> { ["paymentReference"] = kid });
    }

    /// <summary>A payment reference on a payment must be a valid KID (2 to 25 digits with a correct check digit).</summary>
    [OnValidate(Payment)]
    public ValidationResult ValidatePaymentReference(Record payment, Erp.Sdk.AppContext ctx) =>
        payment.Get<string>("paymentReference") is { Length: > 0 } reference && !Kid.IsValid(reference)
            ? ValidationResult.Error("paymentReference", $"{reference} er ikke en gyldig KID (2–25 siffer med riktig kontrollsiffer).")
            : ValidationResult.Ok;

    // ------------------------------------------------------------------ SAF-T export

    [OnValidate(SaftExport)]
    public ValidationResult ValidateExport(Record export, Erp.Sdk.AppContext ctx) =>
        export.Get<DateOnly?>("fromDate") is { } from && export.Get<DateOnly?>("toDate") is { } to && to < from
            ? ValidationResult.Error("toDate", "Til dato kan ikke være før fra dato.")
            : ValidationResult.Ok;

    /// <summary>A new export is made in the background (it can take longer than a hook may), so the request is an event.</summary>
    [OnAfterSave(SaftExport)]
    public async Task AfterExportSaved(Record export, Erp.Sdk.AppContext ctx)
    {
        if (export.Get<string>("status") == "requested")
        {
            await ctx.Events.PublishAsync("norway.saftexport.requested@1", export.Id.ToString(), new { exportId = export.Id });
        }
    }

    /// <summary>
    /// Makes the SAF-T Financial file for the period, checks it against Skatteetaten's schema, stores it and records the
    /// result on the request. A file that does not pass the schema is never handed out. Safe to deliver again.
    /// </summary>
    [OnEvent("norway.saftexport.requested@1")]
    public async Task OnSaftExportRequested(SaftExportRequested e, Erp.Sdk.AppContext ctx)
    {
        var export = await ctx.Data.GetAsync(SaftExport, e.ExportId);
        if (export is null || export.Get<string>("status") != "requested")
        {
            return;
        }

        var from = export.Get<DateOnly>("fromDate");
        var to = export.Get<DateOnly>("toDate");
        var result = new Dictionary<string, object?>();
        try
        {
            var data = await SaftExporter.LoadAsync(ctx, from, to);
            var xml = SaftXml.Write(data);
            var errors = SaftXml.Validate(xml);
            if (errors.Count > 0)
            {
                throw new SaftExportException($"Filen følger ikke Skatteetatens skjema (v{SaftXml.SchemaVersion}): {string.Join(" | ", errors.Take(5))}");
            }

            var fileName = $"SAF-T_{data.Company.RegistrationNumber ?? "firma"}_{from:yyyyMMdd}-{to:yyyyMMdd}.xml";
            result["file"] = await ctx.Documents.StoreDocumentAsync(fileName, "application/xml", xml);
            result["fileName"] = fileName;
            result["transactionCount"] = data.Transactions.Count;
            result["message"] = $"SAF-T Regnskap v{SaftXml.SchemaVersion} ({data.Transactions.Count} bilag) er laget og sjekket mot Skatteetatens skjema.";
            result["status"] = "done";
        }
        catch (SaftExportException ex)
        {
            result["message"] = ex.Message;
            result["status"] = "failed";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Whatever else goes wrong is recorded on the request too, so the person asking is not left waiting
            result["message"] = $"Eksporten feilet: {ex.Message}";
            result["status"] = "failed";
        }

        var current = await ctx.Data.GetAsync(SaftExport, e.ExportId) ?? export;
        await ctx.Data.UpdateAsync(SaftExport, current.Id, current.RowVersion, result);
    }

    // ------------------------------------------------------------------ the VAT return per term

    [OnValidate(VatReturn)]
    public ValidationResult ValidateReturn(Record vatReturn, Erp.Sdk.AppContext ctx) =>
        vatReturn.Get<DateOnly?>("startDate") is { } from && vatReturn.Get<DateOnly?>("endDate") is { } to && to < from
            ? ValidationResult.Error("endDate", "Til dato kan ikke være før fra dato.")
            : ValidationResult.Ok;

    /// <summary>
    /// Calculating adds up the VAT entries of the term per code and sets the totals; reopening removes the lines again.
    /// Output VAT minus input VAT is the amount to pay (negative = refund).
    /// </summary>
    [OnAfterSave(VatReturn)]
    public async Task AfterReturnSaved(Record vatReturn, Erp.Sdk.AppContext ctx)
    {
        var lines = await ctx.Data.QueryAsync(VatReturnLine, $"vatReturn eq '{vatReturn.Id}'", top: 1000);
        switch (vatReturn.Get<string>("status"))
        {
            case "calculated" when lines.Count == 0:
                await CalculateAsync(vatReturn, ctx);
                break;
            case "draft" when lines.Count > 0:
                foreach (var line in lines)
                {
                    await ctx.Data.DeleteAsync(VatReturnLine, line.Id, line.RowVersion);
                }

                await SetTotalsAsync(vatReturn, ctx, 0, 0);
                break;
        }
    }

    private static async Task CalculateAsync(Record vatReturn, Erp.Sdk.AppContext ctx)
    {
        var from = vatReturn.Get<DateOnly>("startDate");
        var to = vatReturn.Get<DateOnly>("endDate");
        var entries = await ctx.Data.QueryAsync(
            VatEntry, $"entryDate ge '{from:yyyy-MM-dd}' and entryDate le '{to:yyyy-MM-dd}'", top: 5000);

        decimal output = 0, input = 0;
        foreach (var group in entries.GroupBy(x => x.Get<Guid>("vatCode")))
        {
            var code = await ctx.Data.GetAsync(VatCode, group.Key);
            var basis = group.Sum(x => x.Get<decimal>("basis"));
            var vat = group.Sum(x => x.Get<decimal>("vatAmount"));
            if (code?.Get<string>("direction") == "output")
            {
                output += vat;
            }
            else
            {
                input += vat;
            }

            await ctx.Data.CreateAsync(VatReturnLine, new Dictionary<string, object?>
            {
                ["vatReturn"] = vatReturn.Id,
                ["vatCode"] = group.Key,
                ["basis"] = basis,
                ["vatAmount"] = vat
            });
        }

        await SetTotalsAsync(vatReturn, ctx, output, input);
    }

    private static async Task SetTotalsAsync(Record vatReturn, Erp.Sdk.AppContext ctx, decimal output, decimal input)
    {
        var current = await ctx.Data.GetAsync(VatReturn, vatReturn.Id) ?? vatReturn;
        await ctx.Data.UpdateAsync(VatReturn, current.Id, current.RowVersion, new Dictionary<string, object?>
        {
            ["outputVat"] = output,
            ["inputVat"] = input,
            ["payable"] = output - input
        });
    }
}
