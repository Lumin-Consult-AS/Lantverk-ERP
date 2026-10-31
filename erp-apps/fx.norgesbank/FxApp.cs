using Erp.Sdk;

namespace Fx.NorgesBank;

/// <summary>
/// Fetches Norges Bank's daily exchange rates and stores them as <c>core.exchangerate</c> (source "Norges Bank"), so the
/// accounting app finds a rate on any voucher date. The core runs the command daily (see the schedule in the manifest).
/// It is safe to run again: rates already stored are left alone, and each run also fills gaps from the last two weeks
/// (holidays, a day the service was down).
/// </summary>
public sealed class FxApp(IExchangeRateFeed feed) : AppBase
{
    public const string Source = "Norges Bank";
    private static readonly string[] DefaultCurrencies = ["EUR", "USD", "SEK", "DKK", "GBP"];
    private const int LookbackDays = 14;

    [Command("fx.norgesbank.update")]
    public async Task<CommandResult> Update(CommandContext cmd, Erp.Sdk.AppContext ctx)
    {
        var chosen = (await ctx.Data.QueryAsync("fx.setting", top: 1)).FirstOrDefault()?.Get<string>("currencies");
        var currencies = (string.IsNullOrWhiteSpace(chosen) ? DefaultCurrencies : chosen.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(c => c.ToUpperInvariant())
            .Distinct()
            .ToList();

        var from = ctx.Today.AddDays(-LookbackDays);
        var fetched = await feed.GetRatesAsync(currencies, from, CancellationToken.None);

        var existing = (await ctx.Data.QueryAsync(
                "core.exchangerate", $"toCurrency eq 'NOK' and rateDate ge '{from:yyyy-MM-dd}'", top: 5000))
            .Select(r => (r.Get<string>("fromCurrency"), r.Get<DateOnly>("rateDate")))
            .ToHashSet();

        var created = 0;
        foreach (var rate in fetched.Where(r => r.QuoteCurrency == "NOK" && !existing.Contains((r.Currency, r.Date))))
        {
            await ctx.Data.CreateAsync("core.exchangerate", new Dictionary<string, object?>
            {
                ["fromCurrency"] = rate.Currency,
                ["toCurrency"] = rate.QuoteCurrency,
                ["rateDate"] = rate.Date,
                ["rate"] = Math.Round(rate.Rate, 8, MidpointRounding.AwayFromZero),
                ["source"] = Source
            });
            created++;
        }

        return CommandResult.Applied($"{created} nye kurser fra {Source} ({string.Join(", ", currencies)}).");
    }
}
