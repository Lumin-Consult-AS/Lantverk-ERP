using System.Globalization;
using System.Text.Json;

namespace Fx.NorgesBank;

/// <summary>One rate from a feed: one unit of <see cref="Currency"/> is worth <see cref="Rate"/> of <see cref="QuoteCurrency"/> on <see cref="Date"/>.</summary>
public sealed record FeedRate(string Currency, string QuoteCurrency, DateOnly Date, decimal Rate);

/// <summary>A source of daily exchange rates. Norges Bank is the first; others can be added as further adapter apps.</summary>
public interface IExchangeRateFeed
{
    Task<IReadOnlyList<FeedRate>> GetRatesAsync(IReadOnlyCollection<string> currencies, DateOnly from, CancellationToken cancellationToken);
}

/// <summary>
/// Norges Bank's open exchange-rate statistics (SDMX-JSON): daily rates against NOK on business days. Some currencies are
/// quoted per 100 units (<c>UNIT_MULT</c> 2, such as JPY); the rate returned is always per one unit.
/// </summary>
public sealed class NorgesBankFeed(HttpClient http) : IExchangeRateFeed
{
    public async Task<IReadOnlyList<FeedRate>> GetRatesAsync(
        IReadOnlyCollection<string> currencies, DateOnly from, CancellationToken cancellationToken)
    {
        if (currencies.Count == 0)
        {
            return [];
        }

        var url = $"api/data/EXR/B.{string.Join('+', currencies)}.NOK.SP?format=sdmx-json&startPeriod={from:yyyy-MM-dd}&locale=en";
        using var response = await http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return Parse(document.RootElement);
    }

    /// <summary>Reads an SDMX-JSON data message with series keyed by base currency.</summary>
    public static IReadOnlyList<FeedRate> Parse(JsonElement message)
    {
        var data = message.GetProperty("data");
        var structure = data.GetProperty("structure");
        var seriesDimensions = structure.GetProperty("dimensions").GetProperty("series");
        var baseCurrencies = Dimension(seriesDimensions, "BASE_CUR");
        var quoteCurrencies = Dimension(seriesDimensions, "QUOTE_CUR");
        var dates = structure.GetProperty("dimensions").GetProperty("observation")[0].GetProperty("values")
            .EnumerateArray().Select(v => DateOnly.Parse(v.GetProperty("id").GetString()!, CultureInfo.InvariantCulture)).ToList();

        // The multiplier of each series is an attribute: its index into the list of attributes says which one to read.
        var attributeList = structure.GetProperty("attributes").GetProperty("series").EnumerateArray().ToList();
        var multiplierAttribute = attributeList.FindIndex(a => a.GetProperty("id").GetString() == "UNIT_MULT");

        var rates = new List<FeedRate>();
        foreach (var series in data.GetProperty("dataSets")[0].GetProperty("series").EnumerateObject())
        {
            // Keys look like "0:2:0:0": the position of each dimension value.
            var position = series.Name.Split(':').Select(int.Parse).ToArray();
            var currency = baseCurrencies[position[1]];
            var quote = quoteCurrencies[position[2]];

            var multiplier = 0;
            if (multiplierAttribute >= 0)
            {
                var valueIndex = series.Value.GetProperty("attributes")[multiplierAttribute].GetInt32();
                multiplier = int.Parse(attributeList[multiplierAttribute].GetProperty("values")[valueIndex].GetProperty("id").GetString()!, CultureInfo.InvariantCulture);
            }

            var divisor = (decimal)Math.Pow(10, multiplier);
            foreach (var observation in series.Value.GetProperty("observations").EnumerateObject())
            {
                var value = decimal.Parse(observation.Value[0].GetString()!, CultureInfo.InvariantCulture);
                rates.Add(new FeedRate(currency, quote, dates[int.Parse(observation.Name, CultureInfo.InvariantCulture)], value / divisor));
            }
        }

        return rates;
    }

    private static List<string> Dimension(JsonElement seriesDimensions, string id) =>
        seriesDimensions.EnumerateArray().First(d => d.GetProperty("id").GetString() == id)
            .GetProperty("values").EnumerateArray().Select(v => v.GetProperty("id").GetString()!).ToList();
}
