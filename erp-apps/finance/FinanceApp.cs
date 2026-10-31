using Erp.Sdk;

namespace Finance;

public sealed record FiscalYearCreated(Guid FiscalyearId);

/// <summary>
/// The country-independent general ledger. A draft voucher is posted by the status transition <c>post</c>; the app then
/// checks it (balance, period, accounts), and in the same transaction creates the posted entry (gap-free number), the
/// postings and the open items of the sub-ledger. Everything runs through the core's data API, so permissions, tenant
/// isolation and the immutability of posted records are the core's.
/// </summary>
public sealed partial class FinanceApp : AppBase
{
    private const string Voucher = "finance.voucher";
    private const string Line = "finance.voucherline";
    private const string Entry = "finance.journalentry";
    private const string Posting = "finance.posting";
    private const string OpenItem = "finance.openitem";
    private const string Account = "finance.account";
    private const string Settings = "finance.settings";
    private const string ExchangeRate = "core.exchangerate";
    private const string Settlement = "finance.settlement";
    private const string SettlementItem = "finance.settlementitem";

    private static readonly string[] DimensionFields = ["dimension1", "dimension2", "dimension3"];
    private static readonly string[] CurrencyFields = ["currency", "currencyAmount", "currencySide", "exchangeRate"];

    // ------------------------------------------------------------------ vouchers

    [OnValidate(Voucher)]
    public async Task<ValidationResult> ValidateVoucher(Record voucher, Erp.Sdk.AppContext ctx)
    {
        if (voucher.Get<string>("status") != "posted")
        {
            return ValidationResult.Ok;
        }

        var failures = new List<ValidationFailure>();
        var date = voucher.Get<DateOnly?>("voucherDate");
        if (date is null)
        {
            return ValidationResult.Error("voucherDate", "Bilaget mangler dato.");
        }

        if (await FindOpenPeriodAsync(ctx, date.Value) is null)
        {
            failures.Add(new ValidationFailure("voucherDate", $"Det finnes ingen åpen regnskapsperiode for {date:yyyy-MM-dd}."));
        }

        var lines = await ctx.Data.QueryAsync(Line, $"voucher eq '{voucher.Id}'", top: 1000);
        if (lines.Count < 2)
        {
            failures.Add(new ValidationFailure("lines", "Et bilag må ha minst to linjer."));
        }

        decimal debit = 0, credit = 0;
        string? baseCurrency = null;
        var baseCurrencyLoaded = false;
        var accounts = new Dictionary<Guid, Record?>();
        foreach (var line in lines)
        {
            var d = line.Get<decimal>("debit");
            var c = line.Get<decimal>("credit");
            if ((d > 0) == (c > 0))
            {
                failures.Add(new ValidationFailure("lines", "Hver linje må ha enten debet eller kredit (ikke begge, ikke ingen)."));
            }

            debit += d;
            credit += c;

            var accountId = line.Get<Guid>("account");
            if (!accounts.TryGetValue(accountId, out var account))
            {
                accounts[accountId] = account = await ctx.Data.GetAsync(Account, accountId);
            }

            if (account is null || !account.Get<bool>("isActive"))
            {
                failures.Add(new ValidationFailure("lines", "En linje bruker en konto som ikke finnes eller er inaktiv."));
            }
            else if (account.Get<string>("controlType") is "receivable" or "payable" && line.Get<Guid?>("party") is null)
            {
                failures.Add(new ValidationFailure("lines", $"Konto {account.Get<string>("number")} er en reskontrokonto, så linjen må ha en part."));
            }

            var currency = line.Get<string>("currency");
            if (!string.IsNullOrEmpty(currency))
            {
                if (!baseCurrencyLoaded)
                {
                    baseCurrency = await GetBaseCurrencyAsync(ctx);
                    baseCurrencyLoaded = true;
                }

                var foreignAmount = line.Get<decimal>("currencyAmount");
                var rate = line.Get<decimal>("exchangeRate");
                if (baseCurrency is null || currency == baseCurrency || foreignAmount <= 0 || rate <= 0)
                {
                    failures.Add(new ValidationFailure("lines", $"Linjen i {currency} er ufullstendig: den trenger beløp i valuta og kurs, og valutaen må være en annen enn bokføringsvalutaen."));
                }
                else
                {
                    var expected = Math.Round(foreignAmount * rate, 2, MidpointRounding.AwayFromZero);
                    var actual = d > 0 ? d : c;
                    if (actual != expected)
                    {
                        failures.Add(new ValidationFailure(
                            "lines", $"Linjen i {currency} stemmer ikke med kursen: {foreignAmount:0.00} × {rate} = {expected:0.00} {baseCurrency}, men linjen har {actual:0.00}."));
                    }
                }
            }
        }

        if (debit != credit)
        {
            failures.Add(new ValidationFailure("lines", $"Bilaget går ikke i balanse: debet {debit:0.00}, kredit {credit:0.00}."));
        }

        return failures.Count == 0 ? ValidationResult.Ok : ValidationResult.Errors(failures.DistinctBy(f => f.Message));
    }

    [OnAfterSave(Voucher)]
    public async Task AfterVoucherSaved(Record voucher, Erp.Sdk.AppContext ctx)
    {
        switch (voucher.Get<string>("status"))
        {
            case "posted":
                await PostAsync(voucher, ctx);
                break;
            case "draft":
                await FillReversalLinesAsync(voucher, ctx);
                break;
        }
    }

    /// <summary>Posting: the posted entry, its postings and the open items. Runs once; the entry is unique per voucher.</summary>
    private static async Task PostAsync(Record voucher, Erp.Sdk.AppContext ctx)
    {
        if ((await ctx.Data.QueryAsync(Entry, $"voucher eq '{voucher.Id}'", top: 1)).Count > 0)
        {
            return;
        }

        var date = voucher.Get<DateOnly>("voucherDate");
        var period = await FindOpenPeriodAsync(ctx, date)
            ?? throw new InvalidOperationException("No open period for the voucher date.");

        var fields = new Dictionary<string, object?>
        {
            ["voucher"] = voucher.Id,
            ["entryDate"] = date,
            ["description"] = voucher.Get<string>("description"),
            ["fiscalPeriod"] = period.Id
        };
        if (voucher.Get<Guid?>("reverses") is { } reversed)
        {
            fields["reversalOf"] = reversed;
        }

        var entry = await ctx.Data.CreateAsync(Entry, fields);

        var lines = await ctx.Data.QueryAsync(Line, $"voucher eq '{voucher.Id}'", orderBy: "created_at", top: 1000);
        var accounts = new Dictionary<Guid, Record>();
        foreach (var line in lines)
        {
            var accountId = line.Get<Guid>("account");
            if (!accounts.TryGetValue(accountId, out var account))
            {
                accounts[accountId] = account = (await ctx.Data.GetAsync(Account, accountId))!;
            }

            var posting = new Dictionary<string, object?>
            {
                ["entry"] = entry.Id,
                ["postingDate"] = date,
                ["account"] = accountId,
                ["description"] = line.Get<string>("description") ?? voucher.Get<string>("description"),
                ["debit"] = line.Get<decimal>("debit"),
                ["credit"] = line.Get<decimal>("credit"),
                ["party"] = line.Get<Guid?>("party"),
                ["currency"] = line.Get<string>("currency"),
                ["currencyAmount"] = line.Get<decimal?>("currencyAmount"),
                ["exchangeRate"] = line.Get<decimal?>("exchangeRate"),
                ["fiscalPeriod"] = period.Id
            };
            foreach (var dimension in DimensionFields)
            {
                posting[dimension] = line.Get<Guid?>(dimension);
            }

            var saved = await ctx.Data.CreateAsync(Posting, posting);

            if (account.Get<string>("controlType") is "receivable" or "payable" && line.Get<Guid?>("party") is { } party)
            {
                var amount = line.Get<decimal>("debit") - line.Get<decimal>("credit");
                await ctx.Data.CreateAsync(OpenItem, new Dictionary<string, object?>
                {
                    ["posting"] = saved.Id,
                    ["party"] = party,
                    ["account"] = accountId,
                    ["dueDate"] = line.Get<DateOnly?>("dueDate") ?? date,
                    ["amount"] = amount,
                    ["remaining"] = amount,
                    ["status"] = "open"
                });
            }

            // The draft line is now history too: it can no longer be changed.
            await ctx.Data.UpdateAsync(Line, line.Id, line.RowVersion, new Dictionary<string, object?> { ["status"] = "posted" });
        }

        await ctx.Events.PublishAsync("finance.journal.posted@1", entry.Id.ToString(), new { entryId = entry.Id, voucherId = voucher.Id });
    }

    /// <summary>A draft voucher that reverses a posted entry gets that entry's postings with debit and credit swapped.</summary>
    private static async Task FillReversalLinesAsync(Record voucher, Erp.Sdk.AppContext ctx)
    {
        if (voucher.Get<Guid?>("reverses") is not { } entryId
            || (await ctx.Data.QueryAsync(Line, $"voucher eq '{voucher.Id}'", top: 1)).Count > 0)
        {
            return;
        }

        var postings = await ctx.Data.QueryAsync(Posting, $"entry eq '{entryId}'", orderBy: "created_at", top: 1000);
        foreach (var p in postings)
        {
            var line = new Dictionary<string, object?>
            {
                ["voucher"] = voucher.Id,
                ["account"] = p.Get<Guid>("account"),
                ["description"] = p.Get<string>("description"),
                ["debit"] = p.Get<decimal>("credit"),
                ["credit"] = p.Get<decimal>("debit"),
                ["party"] = p.Get<Guid?>("party"),
                ["currency"] = p.Get<string>("currency"),
                ["currencyAmount"] = p.Get<decimal?>("currencyAmount"),
                ["currencySide"] = p.Get<string>("currency") is null ? null : (p.Get<decimal>("credit") > 0 ? "debit" : "credit"),
                ["exchangeRate"] = p.Get<decimal?>("exchangeRate")
            };
            foreach (var dimension in DimensionFields)
            {
                line[dimension] = p.Get<Guid?>(dimension);
            }

            await ctx.Data.CreateAsync(Line, line);
        }
    }

    /// <summary>The lines of a draft can be changed; those of a posted voucher cannot (except when the app itself posts them).</summary>
    [OnValidate(Line)]
    public async Task<ValidationResult> ValidateLine(Record line, Erp.Sdk.AppContext ctx)
    {
        var onlyStatus = line.Fields.Keys.All(k => string.Equals(k, "status", StringComparison.OrdinalIgnoreCase));
        if (onlyStatus)
        {
            return ValidationResult.Ok;
        }

        var voucherId = line.Get<Guid?>("voucher") ?? (await ctx.Data.GetAsync(Line, line.Id))?.Get<Guid?>("voucher");
        if (voucherId is { } id && (await ctx.Data.GetAsync(Voucher, id))?.Get<string>("status") == "posted")
        {
            return ValidationResult.Error("voucher", "Bilaget er bokført; linjene kan ikke endres. Korriger med et motpostert bilag.");
        }

        return ValidationResult.Ok;
    }

    // ------------------------------------------------------------------ foreign currency

    /// <summary>
    /// A line in a foreign currency is given as currency, amount and side. The app finds the rate on the voucher date
    /// (or uses the one set on the line) and works out debit or credit in the base currency. The rate is then fixed on the
    /// line; posting checks that the line still agrees with it.
    /// </summary>
    [OnBeforeSave(Line)]
    public async Task<ValidationResult> ConvertForeignLine(Record line, Erp.Sdk.AppContext ctx)
    {
        if (!CurrencyFields.Any(f => line.Fields.ContainsKey(f)))
        {
            return ValidationResult.Ok;
        }

        // Null for a new line; for a change, the fields the change does not mention come from the stored line.
        var existing = await ctx.Data.GetAsync(Line, line.Id);
        T? Value<T>(string field) => line.Fields.ContainsKey(field) ? line.Get<T>(field) : existing is null ? default : existing.Get<T>(field);

        var currency = Value<string>("currency");
        if (string.IsNullOrEmpty(currency))
        {
            return ValidationResult.Ok;
        }

        var baseCurrency = await GetBaseCurrencyAsync(ctx);
        if (baseCurrency is null)
        {
            return ValidationResult.Error("currency", "Sett bokføringsvalutaen under Regnskapsinnstillinger før du bruker fremmed valuta.");
        }

        if (currency == baseCurrency)
        {
            // Not a foreign currency after all: the line is an ordinary one.
            foreach (var field in CurrencyFields)
            {
                line.Set(field, null);
            }

            return ValidationResult.Ok;
        }

        var amount = Value<decimal?>("currencyAmount");
        var side = Value<string>("currencySide");
        if (amount is not > 0 || side is not ("debit" or "credit"))
        {
            return ValidationResult.Error("currencyAmount", "En linje i fremmed valuta må ha beløp i valuta (over 0) og side (debet eller kredit).");
        }

        // A rate set by hand wins; otherwise the stored rate is kept while the currency is the same; otherwise it is looked up.
        decimal? rate = line.Fields.ContainsKey("exchangeRate") ? line.Get<decimal?>("exchangeRate") : null;
        if (rate is not > 0 && existing is not null && existing.Get<string>("currency") == currency)
        {
            rate = existing.Get<decimal?>("exchangeRate");
        }

        if (rate is not > 0)
        {
            var voucherId = Value<Guid?>("voucher");
            var voucher = voucherId is { } id ? await ctx.Data.GetAsync(Voucher, id) : null;
            if (voucher?.Get<DateOnly?>("voucherDate") is not { } date)
            {
                return ValidationResult.Error("exchangeRate", "Kursen kan ikke slås opp fordi bilaget eller bilagsdatoen mangler. Sett kursen på linjen.");
            }

            rate = await FindRateAsync(ctx, currency, baseCurrency, date);
            if (rate is null)
            {
                return ValidationResult.Error(
                    "exchangeRate", $"Fant ingen kurs for {currency} til {baseCurrency} på {date:yyyy-MM-dd} eller tidligere. Legg inn kursen, eller sett den på linjen.");
            }
        }

        var converted = Math.Round(amount.Value * rate.Value, 2, MidpointRounding.AwayFromZero);
        line.Set("exchangeRate", rate.Value);
        line.Set("debit", side == "debit" ? converted : 0m);
        line.Set("credit", side == "credit" ? converted : 0m);
        return ValidationResult.Ok;
    }

    /// <summary>The company's base currency, or null when the settings have not been filled in.</summary>
    private static async Task<string?> GetBaseCurrencyAsync(Erp.Sdk.AppContext ctx) =>
        (await ctx.Data.QueryAsync(Settings, top: 1)).FirstOrDefault()?.Get<string>("baseCurrency");

    /// <summary>
    /// The newest rate on or before the date: one unit of <paramref name="from"/> is worth that many <paramref name="to"/>.
    /// If only the opposite pair has a rate, its inverse is used.
    /// </summary>
    private static async Task<decimal?> FindRateAsync(Erp.Sdk.AppContext ctx, string from, string to, DateOnly date)
    {
        var day = date.ToString("yyyy-MM-dd");
        var direct = await ctx.Data.QueryAsync(
            ExchangeRate, $"fromCurrency eq '{from}' and toCurrency eq '{to}' and rateDate le '{day}'", orderBy: "rateDate desc", top: 1);
        if (direct.FirstOrDefault()?.Get<decimal?>("rate") is > 0 and var rate)
        {
            return rate;
        }

        var inverse = await ctx.Data.QueryAsync(
            ExchangeRate, $"fromCurrency eq '{to}' and toCurrency eq '{from}' and rateDate le '{day}'", orderBy: "rateDate desc", top: 1);
        return inverse.FirstOrDefault()?.Get<decimal?>("rate") is > 0 and var opposite
            ? Math.Round(1m / opposite, 8, MidpointRounding.AwayFromZero)
            : null;
    }

    // ------------------------------------------------------------------ settlement of open items

    [OnValidate(Settlement)]
    public async Task<ValidationResult> ValidateSettlement(Record settlement, Erp.Sdk.AppContext ctx)
    {
        if (settlement.Get<string>("status") != "completed")
        {
            return ValidationResult.Ok;
        }

        var items = await ctx.Data.QueryAsync(SettlementItem, $"settlement eq '{settlement.Id}'", top: 1000);
        if (items.Count < 2)
        {
            return ValidationResult.Error("items", "En utligning må ha minst to linjer.");
        }

        var failures = new List<ValidationFailure>();
        decimal total = 0;
        Guid? party = null;
        foreach (var item in items)
        {
            var open = await ctx.Data.GetAsync(OpenItem, item.Get<Guid>("openItem"));
            var amount = item.Get<decimal>("amount");
            total += amount;
            if (open is null || open.Get<string>("status") != "open")
            {
                failures.Add(new ValidationFailure("items", "En linje peker på en åpen post som ikke finnes eller allerede er utlignet."));
                continue;
            }

            var remaining = open.Get<decimal>("remaining");
            if (amount == 0 || Math.Sign(amount) != Math.Sign(remaining) || Math.Abs(amount) > Math.Abs(remaining))
            {
                failures.Add(new ValidationFailure("items", $"Utlignet beløp {amount:0.00} passer ikke med åpent beløp {remaining:0.00}."));
            }

            var openParty = open.Get<Guid>("party");
            party ??= openParty;
            if (party != openParty)
            {
                failures.Add(new ValidationFailure("items", "Alle åpne poster i en utligning må tilhøre samme part."));
            }
        }

        if (total != 0)
        {
            failures.Add(new ValidationFailure("items", $"Utligningen går ikke opp: summen er {total:0.00}, og skal være 0."));
        }

        return failures.Count == 0 ? ValidationResult.Ok : ValidationResult.Errors(failures.DistinctBy(f => f.Message));
    }

    [OnAfterSave(Settlement)]
    public async Task AfterSettlementSaved(Record settlement, Erp.Sdk.AppContext ctx)
    {
        if (settlement.Get<string>("status") != "completed")
        {
            return;
        }

        foreach (var item in await ctx.Data.QueryAsync(SettlementItem, $"settlement eq '{settlement.Id}'", top: 1000))
        {
            var open = (await ctx.Data.GetAsync(OpenItem, item.Get<Guid>("openItem")))!;
            var remaining = open.Get<decimal>("remaining") - item.Get<decimal>("amount");
            await ctx.Data.UpdateAsync(OpenItem, open.Id, open.RowVersion, new Dictionary<string, object?>
            {
                ["remaining"] = remaining,
                ["status"] = remaining == 0 ? "settled" : "open"
            });
        }

        await ctx.Events.PublishAsync("finance.settlement.completed@1", settlement.Id.ToString(), new { settlementId = settlement.Id });
    }

    // ------------------------------------------------------------------ periods

    /// <summary>A new fiscal year gets one period per calendar month from its start date (idempotent: delivery is at least once).</summary>
    [OnEvent("core.fiscalyear.created@1")]
    public async Task OnFiscalYearCreated(FiscalYearCreated e, Erp.Sdk.AppContext ctx)
    {
        var year = await ctx.Data.GetAsync("core.fiscalyear", e.FiscalyearId);
        if (year is null
            || (await ctx.Data.QueryAsync("core.fiscalperiod", $"fiscalYear eq '{year.Id}'", top: 1)).Count > 0)
        {
            return;
        }

        var start = year.Get<DateOnly>("startDate");
        var end = year.Get<DateOnly>("endDate");
        for (var number = 1; start.AddMonths(number - 1) <= end && number <= 14; number++)
        {
            var from = start.AddMonths(number - 1);
            var to = from.AddMonths(1).AddDays(-1);
            await ctx.Data.CreateAsync("core.fiscalperiod", new Dictionary<string, object?>
            {
                ["fiscalYear"] = year.Id,
                ["number"] = number,
                ["name"] = from.ToString("yyyy-MM"),
                ["startDate"] = from,
                ["endDate"] = to > end ? end : to
            });
        }
    }

    private static async Task<Record?> FindOpenPeriodAsync(Erp.Sdk.AppContext ctx, DateOnly date)
    {
        var day = date.ToString("yyyy-MM-dd");
        var periods = await ctx.Data.QueryAsync(
            "core.fiscalperiod", $"startDate le '{day}' and endDate ge '{day}' and status eq 'open'", top: 1);
        return periods.FirstOrDefault();
    }
}
