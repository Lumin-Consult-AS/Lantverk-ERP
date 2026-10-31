using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Erp.Core.Domain.Tenancy;
using Erp.Core.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Erp.Core.Tests;

/// <summary>Fiscal years, periods and exchange rates (phase 2, step 2): the frame the accounting app works within.</summary>
[Collection(PostgresDatabaseCollection.Name)]
public sealed class PeriodsAndRatesTests(PostgresDatabaseFixture fixture)
{
    private const string TestOidcSigningKey = "test-only-external-oidc-signing-key-at-least-32-bytes!";
    private const string TestOidcIssuer = "https://oidc.test.local/realms/erp-dev";
    private const string TestOidcAudience = "erp-web";

    private static string Url(string objectId) => $"/api/v1/{DemoTenantSeeder.DemoCompanyId}/{objectId}";

    [Fact]
    public async Task ACompanyHasAFiscalYearWithPeriods_AndAPeriodCanBeClosedAndReopened()
    {
        await using var factory = CreateFactory();
        using var client = await LoginAsync(factory);

        try
        {
            await InstallAsync(client, "core.organization");

            var year = await CreateAsync(client, "core.fiscalyear", new { year = 2026, startDate = "2026-01-01", endDate = "2026-12-31" });
            Assert.Equal("open", Status(year));

            // Twelve monthly periods
            var periods = new List<JsonElement>();
            for (var month = 1; month <= 12; month++)
            {
                var start = new DateOnly(2026, month, 1);
                periods.Add(await CreateAsync(client, "core.fiscalperiod", new
                {
                    fiscalYear = Id(year),
                    number = month,
                    name = start.ToString("MMMM yyyy"),
                    startDate = start.ToString("yyyy-MM-dd"),
                    endDate = start.AddMonths(1).AddDays(-1).ToString("yyyy-MM-dd")
                }));
            }

            var listed = await client.GetFromJsonAsync<JsonElement>($"{Url("core.fiscalperiod")}?filter=fiscalYear eq '{Id(year)}'&orderBy=number&top=50");
            Assert.Equal(12, listed.GetArrayLength());
            Assert.All(listed.EnumerateArray(), p => Assert.Equal("open", Status(p)));

            // The same year, and the same period number twice, are refused
            using (var sameYear = await client.PostAsJsonAsync(Url("core.fiscalyear"), new { year = 2026, startDate = "2026-01-01", endDate = "2026-12-31" }))
            {
                Assert.Equal(HttpStatusCode.BadRequest, sameYear.StatusCode);
            }

            using (var sameNumber = await client.PostAsJsonAsync(
                       Url("core.fiscalperiod"), new { fiscalYear = Id(year), number = 3, startDate = "2026-03-01", endDate = "2026-03-31" }))
            {
                Assert.Equal(HttpStatusCode.BadRequest, sameNumber.StatusCode);
            }

            // March is closed, and can be opened again; the other periods are not affected
            var march = periods[2];
            var closed = await TransitionAsync(client, march, "close");
            Assert.Equal("closed", Status(closed));
            var other = await client.GetFromJsonAsync<JsonElement>($"{Url("core.fiscalperiod")}/{Id(periods[3])}");
            Assert.Equal("open", Status(other));

            using (var closeAgain = await client.PostAsync($"{Url("core.fiscalperiod")}/{Id(march)}/transitions/close", null))
            {
                Assert.False(closeAgain.IsSuccessStatusCode, "A closed period cannot be closed again.");
            }

            var reopened = await TransitionAsync(client, closed, "reopen");
            Assert.Equal("open", Status(reopened));
        }
        finally
        {
            await client.DeleteAsync("/api/v1/apps/core.organization");
        }
    }

    [Fact]
    public async Task ARateIsFoundPerDateAndCurrencyPair_ByTheLatestOnOrBeforeTheDate()
    {
        await using var factory = CreateFactory();
        using var client = await LoginAsync(factory);

        try
        {
            await InstallAsync(client, "core.currencies");

            await CreateAsync(client, "core.exchangerate", new { fromCurrency = "EUR", toCurrency = "NOK", rateDate = "2026-10-01", rate = 11.5m });
            await CreateAsync(client, "core.exchangerate", new { fromCurrency = "EUR", toCurrency = "NOK", rateDate = "2026-10-15", rate = 11.7m, source = "Norges Bank" });
            await CreateAsync(client, "core.exchangerate", new { fromCurrency = "EUR", toCurrency = "NOK", rateDate = "2026-10-30", rate = 11.6m });
            await CreateAsync(client, "core.exchangerate", new { fromCurrency = "USD", toCurrency = "NOK", rateDate = "2026-10-15", rate = 10.4m });

            Assert.Equal(11.7m, await RateAsync(client, "EUR", "NOK", "2026-10-20"));  // the 15th is the latest on or before the 20th
            Assert.Equal(11.5m, await RateAsync(client, "EUR", "NOK", "2026-10-01"));  // exactly on the date
            Assert.Equal(11.6m, await RateAsync(client, "EUR", "NOK", "2026-11-30"));  // the newest rate holds until a new one comes
            Assert.Equal(10.4m, await RateAsync(client, "USD", "NOK", "2026-10-20"));  // another pair is not mixed in
            Assert.Null(await RateAsync(client, "EUR", "NOK", "2026-09-30"));          // nothing before the first rate
            Assert.Null(await RateAsync(client, "NOK", "EUR", "2026-10-20"));          // the pair has a direction

            // One rate per pair and date; codes are upper case ISO codes; the rate is positive
            foreach (var bad in new object[]
                     {
                         new { fromCurrency = "EUR", toCurrency = "NOK", rateDate = "2026-10-15", rate = 12m },
                         new { fromCurrency = "eur", toCurrency = "NOK", rateDate = "2026-10-16", rate = 12m },
                         new { fromCurrency = "EUR", toCurrency = "NOK", rateDate = "2026-10-17", rate = 0m }
                     })
            {
                using var res = await client.PostAsJsonAsync(Url("core.exchangerate"), bad);
                Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            }
        }
        finally
        {
            await client.DeleteAsync("/api/v1/apps/core.currencies");
        }
    }

    private static async Task<decimal?> RateAsync(HttpClient client, string from, string to, string date)
    {
        var rows = await client.GetFromJsonAsync<JsonElement>(
            $"{Url("core.exchangerate")}?filter=fromCurrency eq '{from}' and toCurrency eq '{to}' and rateDate le '{date}'&orderBy=rateDate desc&top=1");
        return rows.GetArrayLength() == 0 ? null : rows[0].GetProperty("fields").GetProperty("rate").GetDecimal();
    }

    private static string Status(JsonElement record) => record.GetProperty("fields").GetProperty("status").GetString()!;

    private static Guid Id(JsonElement record) => record.GetProperty("id").GetGuid();

    private static async Task InstallAsync(HttpClient client, string appId)
    {
        using var res = await client.PostAsync($"/api/v1/apps/install-builtin/{appId}", null);
        Assert.True(res.IsSuccessStatusCode, $"{appId}: {await res.Content.ReadAsStringAsync()}");
    }

    private static async Task<JsonElement> CreateAsync(HttpClient client, string objectId, object body)
    {
        using var res = await client.PostAsJsonAsync(Url(objectId), body);
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{objectId}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<JsonElement> TransitionAsync(HttpClient client, JsonElement record, string transition)
    {
        using var res = await client.PostAsync($"{Url("core.fiscalperiod")}/{Id(record)}/transitions/{transition}", null);
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, text);
        return JsonDocument.Parse(text).RootElement.Clone();
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
                    ["Auth:OidcAudience"] = TestOidcAudience
                });
            });
        });
    }
}
