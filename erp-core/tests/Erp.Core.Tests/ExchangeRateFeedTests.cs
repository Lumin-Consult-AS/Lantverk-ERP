using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Erp.Core.Infrastructure.Apps;
using Erp.Core.Infrastructure.Persistence;
using Erp.Sdk;
using Fx.NorgesBank;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Erp.Core.Tests;

/// <summary>The Norges Bank feed: reading the real message format, and the daily update through the schedule.</summary>
[Collection(PostgresDatabaseCollection.Name)]
public sealed class ExchangeRateFeedTests(PostgresDatabaseFixture fixture)
{
    private const string TestOidcSigningKey = "test-only-external-oidc-signing-key-at-least-32-bytes!";
    private const string TestOidcIssuer = "https://oidc.test.local/realms/erp-dev";
    private const string TestOidcAudience = "erp-web";

    // Trimmed from an actual answer from data.norges-bank.no (EUR, USD, JPY): JPY is quoted per 100 units (UNIT_MULT 2).
    private const string OneDay = """
        {"data":{"dataSets":[{"series":{
          "0:0:0:0":{"attributes":[0,0,0,0],"observations":{"0":["5.8698"]}},
          "0:1:0:0":{"attributes":[0,0,1,0],"observations":{"0":["9.2302"]}},
          "0:2:0:0":{"attributes":[0,0,1,0],"observations":{"0":["10.312"]}}}}],
         "structure":{"dimensions":{"series":[
            {"id":"FREQ","values":[{"id":"B"}]},
            {"id":"BASE_CUR","values":[{"id":"JPY"},{"id":"USD"},{"id":"EUR"}]},
            {"id":"QUOTE_CUR","values":[{"id":"NOK"}]},
            {"id":"TENOR","values":[{"id":"SP"}]}],
          "observation":[{"id":"TIME_PERIOD","values":[{"id":"2026-10-30"}]}]},
          "attributes":{"series":[
            {"id":"DECIMALS","values":[{"id":"4"}]},
            {"id":"CALCULATED","values":[{"id":"false"}]},
            {"id":"UNIT_MULT","values":[{"id":"2"},{"id":"0"}]},
            {"id":"COLLECTION","values":[{"id":"C"}]}]}}}}
        """;

    // One currency over several business days (weekend missing).
    private const string SeveralDays = """
        {"data":{"dataSets":[{"series":{"0:0:0:0":{"attributes":[0,0,0,0],"observations":{"0":["10.5245"],"1":["10.512"],"2":["10.488"]}}}}],
         "structure":{"dimensions":{"series":[
            {"id":"FREQ","values":[{"id":"B"}]},
            {"id":"BASE_CUR","values":[{"id":"EUR"}]},
            {"id":"QUOTE_CUR","values":[{"id":"NOK"}]},
            {"id":"TENOR","values":[{"id":"SP"}]}],
          "observation":[{"id":"TIME_PERIOD","values":[{"id":"2026-10-22"},{"id":"2026-10-23"},{"id":"2026-10-26"}]}]},
          "attributes":{"series":[
            {"id":"DECIMALS","values":[{"id":"4"}]},
            {"id":"UNIT_MULT","values":[{"id":"0"}]}]}}}}
        """;

    private sealed class CannedHandler(string body) : HttpMessageHandler
    {
        public Uri? Requested { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class FakeFeed : IExchangeRateFeed
    {
        public List<FeedRate> Rates { get; } = [];
        public bool Down { get; set; }
        public IReadOnlyCollection<string>? AskedFor { get; private set; }

        public Task<IReadOnlyList<FeedRate>> GetRatesAsync(IReadOnlyCollection<string> currencies, DateOnly from, CancellationToken cancellationToken)
        {
            AskedFor = currencies;
            return Down
                ? throw new HttpRequestException("Norges Bank svarer ikke.")
                : Task.FromResult<IReadOnlyList<FeedRate>>(Rates.Where(r => currencies.Contains(r.Currency)).ToList());
        }
    }

    [Fact]
    public async Task TheFeed_ReadsTheNorgesBankMessage_PerOneUnit_AndAsksForTheRightSeries()
    {
        var handler = new CannedHandler(OneDay);
        var feed = new NorgesBankFeed(new HttpClient(handler) { BaseAddress = new Uri("https://data.norges-bank.no/") });

        var rates = await feed.GetRatesAsync(["JPY", "USD", "EUR"], new DateOnly(2026, 10, 20), default);

        Assert.Equal("/api/data/EXR/B.JPY+USD+EUR.NOK.SP", handler.Requested!.AbsolutePath);
        Assert.Contains("startPeriod=2026-10-20", handler.Requested.Query);
        Assert.Contains("format=sdmx-json", handler.Requested.Query);
        Assert.Equal(3, rates.Count);
        Assert.All(rates, r => Assert.Equal("NOK", r.QuoteCurrency));
        Assert.Equal(0.058698m, rates.Single(r => r.Currency == "JPY").Rate); // quoted per 100 JPY, returned per 1
        Assert.Equal(9.2302m, rates.Single(r => r.Currency == "USD").Rate);
        Assert.Equal(10.312m, rates.Single(r => r.Currency == "EUR").Rate);
        Assert.All(rates, r => Assert.Equal(new DateOnly(2026, 10, 30), r.Date));

        var several = NorgesBankFeed.Parse(JsonDocument.Parse(SeveralDays).RootElement);
        Assert.Equal(
            [new DateOnly(2026, 10, 22), new DateOnly(2026, 10, 23), new DateOnly(2026, 10, 26)],
            several.OrderBy(r => r.Date).Select(r => r.Date).ToArray());
        Assert.Equal(10.488m, several.Single(r => r.Date == new DateOnly(2026, 10, 26)).Rate);

        Assert.Empty(await feed.GetRatesAsync([], new DateOnly(2026, 10, 20), default));
    }

    [Fact]
    public async Task TheDailyUpdate_StoresNewRatesOnce_UsesTheChosenCurrencies_AndReportsAFailureForRetry()
    {
        var feed = new FakeFeed();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        feed.Rates.AddRange(
        [
            new FeedRate("EUR", "NOK", today.AddDays(-2), 11.50m),
            new FeedRate("EUR", "NOK", today.AddDays(-1), 11.60m),
            new FeedRate("USD", "NOK", today.AddDays(-1), 10.40m),
            new FeedRate("SEK", "NOK", today.AddDays(-1), 1.05m)
        ]);

        await using var factory = CreateFactory();
        await using var host = await StartAsync(factory, feed);
        using var client = await LoginAsync(factory);

        try
        {
            await InstallBuiltInAsync(client, "core.currencies");
            await InstallBuiltInAsync(client, "fx.norgesbank");

            // The schedule is listed with its daily time
            var schedules = (await client.GetFromJsonAsync<JsonElement>("/api/v1/schedules")).EnumerateArray().ToList();
            var daily = Assert.Single(schedules);
            Assert.Equal("fx.norgesbank.daily", daily.GetProperty("scheduleId").GetString());
            Assert.Equal("15:30", daily.GetProperty("at").GetString());

            // Without settings the default currencies are fetched; each rate is stored once
            var first = await RunAsync(client);
            Assert.Equal("ok", first.GetProperty("status").GetString());
            Assert.Contains("4 nye kurser", first.GetProperty("message").GetString());
            Assert.Contains("EUR", feed.AskedFor!);
            Assert.Equal(4, (await RatesAsync(client)).Count);

            var again = await RunAsync(client);
            Assert.Contains("0 nye kurser", again.GetProperty("message").GetString());
            Assert.Equal(4, (await RatesAsync(client)).Count);

            // A new day brings a new rate; the stored ones are untouched
            feed.Rates.Add(new FeedRate("EUR", "NOK", today, 11.70m));
            Assert.Contains("1 nye kurser", (await RunAsync(client)).GetProperty("message").GetString());
            var eur = (await RatesAsync(client)).Where(r => r.GetProperty("fields").GetProperty("fromCurrency").GetString() == "EUR").ToList();
            Assert.Equal(3, eur.Count);
            Assert.All(eur, r => Assert.Equal("Norges Bank", r.GetProperty("fields").GetProperty("source").GetString()));

            // The tenant can choose which currencies: only those are asked for
            using (var choose = await client.PostAsJsonAsync($"/api/v1/{DemoTenantSeeder.DemoCompanyId}/fx.setting", new { currencies = "usd, eur" }))
            {
                Assert.True(choose.IsSuccessStatusCode, await choose.Content.ReadAsStringAsync());
            }

            await RunAsync(client);
            Assert.Equal(["EUR", "USD"], feed.AskedFor!.OrderBy(c => c).ToArray());

            // When the service is down the run fails with its reason, and the schedule is kept for a retry
            feed.Down = true;
            var failed = await RunAsync(client);
            Assert.Equal("failed", failed.GetProperty("status").GetString());
            Assert.Contains("svarer ikke", failed.GetProperty("message").GetString());
            var listed = (await client.GetFromJsonAsync<JsonElement>("/api/v1/schedules")).EnumerateArray().Single();
            Assert.Equal("failed", listed.GetProperty("lastStatus").GetString());
        }
        finally
        {
            await client.DeleteAsync("/api/v1/apps/fx.norgesbank?purge=true");
            await client.DeleteAsync("/api/v1/apps/core.currencies?purge=true");
        }
    }

    private static async Task<JsonElement> RunAsync(HttpClient client)
    {
        using var res = await client.PostAsync("/api/v1/schedules/fx.norgesbank/fx.norgesbank.daily/run", null);
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<List<JsonElement>> RatesAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/{DemoTenantSeeder.DemoCompanyId}/core.exchangerate?top=500")).EnumerateArray().Select(e => e.Clone()).ToList();

    private static async Task InstallBuiltInAsync(HttpClient client, string appId)
    {
        using var res = await client.PostAsync($"/api/v1/apps/install-builtin/{appId}", null);
        Assert.True(res.IsSuccessStatusCode, $"{appId}: {await res.Content.ReadAsStringAsync()}");
    }

    private static async Task<WebApplication> StartAsync(WebApplicationFactory<Program> core, IExchangeRateFeed feed)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(feed);
        builder.Services.AddErpApp<FxApp>(opts =>
        {
            opts.CoreGrpcAddress = "http://erp-core-loopback";
            opts.ChannelHandlerFactory = () => core.Server.CreateHandler();
        });

        var app = builder.Build();
        app.MapErpApp<FxApp>();
        await app.StartAsync();
        core.Services.GetRequiredService<KubernetesAppOrchestrator>().RegisterEndpoint(
            "fx.norgesbank", "1.0.0", "http://fx-norgesbank:50051", () => app.GetTestServer().CreateHandler());
        return app;
    }

    private static async Task<HttpClient> LoginAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        using var res = await client.PostAsJsonAsync("/api/v1/auth/dev-login", new { });
        res.EnsureSuccessStatusCode();
        var token = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private WebApplicationFactory<Program> CreateFactory()
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:MigrationConnection"] = fixture.MigrationConnectionString,
                    ["ConnectionStrings:ApplicationConnection"] = fixture.ApplicationConnectionString,
                    ["Database:AutoMigrateAndSeed"] = "false",
                    ["Auth:EnableDevLogin"] = "true",
                    ["Auth:OidcTestSigningKey"] = TestOidcSigningKey,
                    ["Auth:OidcTestIssuer"] = TestOidcIssuer,
                    ["Auth:OidcAudience"] = TestOidcAudience,
                    ["Schedules:PollInterval"] = "01:00:00"
                });
            });
        });
    }
}
