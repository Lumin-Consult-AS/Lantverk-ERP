using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Erp.Client.Models;
using Erp.Client.Services;
using Erp.Core.Domain.Tenancy;
using Erp.Core.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace Erp.Core.Tests;

/// <summary>Language and region (ADR-0033): the client shows and reads dates and numbers by region; the API keeps ISO dates and decimal points.</summary>
[Collection(PostgresDatabaseCollection.Name)]
public sealed class RegionTests(PostgresDatabaseFixture fixture)
{
    private const string TestOidcSigningKey = "test-only-external-oidc-signing-key-at-least-32-bytes!";
    private const string TestOidcIssuer = "https://oidc.test.local/realms/erp-dev";
    private const string TestOidcAudience = "erp-web";
    private const char Nbsp = ' ';

    private static RegionService In(string region)
    {
        var service = new RegionService();
        service.SetRegion(region);
        return service;
    }

    // ------------------------------------------------------------------ showing

    [Fact]
    public void Norway_ShowsDatesAndNumbersTheNorwegianWay()
    {
        var no = In("NO");
        Assert.Equal("31.12.2026", no.FormatDate(new DateOnly(2026, 12, 31)));
        Assert.Equal("05.03.2026", no.FormatDate(new DateOnly(2026, 3, 5)));
        Assert.Equal($"1{Nbsp}234,56", no.FormatNumber(1234.56m, 2));
        Assert.Equal($"1{Nbsp}234{Nbsp}567,50", no.FormatNumber(1234567.5m, 2));
        Assert.Equal("0,05", no.FormatNumber(0.05m, 2));
        Assert.Equal($"-12{Nbsp}000,00", no.FormatNumber(-12000m, 2));
        Assert.Equal($"1{Nbsp}250,00", no.FormatNumber(1250m, 2));
        Assert.Equal("11,4321", no.FormatNumber(11.4321m)); // without a scale, the decimals the number has
        Assert.Equal("12", no.FormatNumber(12.000m));
        Assert.Equal("2026", no.FormatInteger(2026)); // a year is not grouped
        Assert.Equal($"12{Nbsp}345", no.FormatInteger(12345));
        Assert.Equal(DayOfWeek.Monday, no.Current.FirstDayOfWeek);
    }

    [Fact]
    public void ADifferentRegion_ShowsTheSameDataItsOwnWay()
    {
        var date = new DateOnly(2026, 12, 31);
        Assert.Equal("31/12/2026", In("GB").FormatDate(date));
        Assert.Equal("1,234.56", In("GB").FormatNumber(1234.56m, 2));
        Assert.Equal("12/31/2026", In("US").FormatDate(date));
        Assert.Equal(DayOfWeek.Sunday, In("US").Current.FirstDayOfWeek);
        Assert.Equal("2026-12-31", In("ZZ").FormatDate(date)); // a region without a profile gets ISO dates
        Assert.Equal("1,234.56", In("ZZ").FormatNumber(1234.56m, 2));
    }

    [Fact]
    public void Display_FollowsTheTypeOfTheField_AndLeavesTextAlone()
    {
        var no = In("NO");
        Assert.Equal("31.12.2026", no.Display(new ClientFieldDefinition { Name = "d", Type = ClientFieldType.Date }, "2026-12-31"));
        Assert.Equal($"1{Nbsp}234,56", no.Display(new ClientFieldDefinition { Name = "a", Type = ClientFieldType.Decimal, Scale = 2 }, JsonDocument.Parse("1234.56").RootElement));
        Assert.Equal("11,5", no.Display(new ClientFieldDefinition { Name = "r", Type = ClientFieldType.Decimal }, JsonDocument.Parse("11.5").RootElement));
        Assert.Equal("31.12.2026 12:00", no.FormatDateTime(new DateTimeOffset(2026, 12, 31, 12, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc));
        Assert.Equal("Leie av lokaler", no.Display(new ClientFieldDefinition { Name = "t", Type = ClientFieldType.String }, "Leie av lokaler"));
        Assert.Equal("2026-13-45", no.Display(new ClientFieldDefinition { Name = "d", Type = ClientFieldType.Date }, "2026-13-45")); // not a date: shown as it is

        // Without a field, a date or a number is recognised by what it looks like; text and identifiers are not touched
        Assert.Equal("31.12.2026", no.Display(null, "2026-12-31"));
        Assert.Equal($"1{Nbsp}234,56", no.Display(null, JsonDocument.Parse("1234.56").RootElement));
        Assert.Equal("K-1001", no.Display(null, "K-1001"));
        Assert.Equal("c0a8012e-0000-4000-8000-000000000001", no.Display(null, "c0a8012e-0000-4000-8000-000000000001"));
    }

    // ------------------------------------------------------------------ reading

    [Theory]
    [InlineData("31.12.2026")]
    [InlineData("31.12.26")]
    [InlineData("311226")]
    [InlineData("31122026")]
    [InlineData("31/12/2026")]
    [InlineData("31 12 2026")]
    [InlineData("2026-12-31")]
    public void Norway_ReadsADateInEveryFormAUserTypes(string typed)
    {
        Assert.True(In("NO").TryParseDate(typed, out var date), typed);
        Assert.Equal(new DateOnly(2026, 12, 31), date);
        Assert.Equal("2026-12-31", RegionService.ToIso(date)); // and what goes to the API is always ISO
    }

    [Theory]
    [InlineData("1.2.2026", 2026, 2, 1)]
    [InlineData("01022026", 2026, 2, 1)]
    [InlineData("29.02.2028", 2028, 2, 29)]
    public void Norway_ReadsDayBeforeMonth(string typed, int year, int month, int day)
    {
        Assert.True(In("NO").TryParseDate(typed, out var date));
        Assert.Equal(new DateOnly(year, month, day), date);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("32.12.2026")]
    [InlineData("31.13.2026")]
    [InlineData("29.02.2026")]
    [InlineData("3112")]
    [InlineData("31.12")]
    [InlineData("31.12.20266")]
    public void ADateThatIsNotOne_IsNotRead(string typed) => Assert.False(In("NO").TryParseDate(typed, out _), typed);

    [Fact]
    public void TheMonthComesFirst_InTheUnitedStates()
    {
        Assert.True(In("US").TryParseDate("12/31/2026", out var date));
        Assert.Equal(new DateOnly(2026, 12, 31), date);
        Assert.True(In("US").TryParseDate("123126", out var short_));
        Assert.Equal(new DateOnly(2026, 12, 31), short_);
        Assert.False(In("US").TryParseDate("31/12/2026", out _));
    }

    [Theory]
    [InlineData("1 234,56", 1234.56)]
    [InlineData("1234,56", 1234.56)]
    [InlineData("1.234,56", 1234.56)]
    [InlineData("1234.56", 1234.56)]
    [InlineData("12.5", 12.5)]
    [InlineData("-3,5", -3.5)]
    [InlineData("1 234 567", 1234567)]
    [InlineData("0,05", 0.05)]
    [InlineData("1250", 1250)]
    public void Norway_ReadsANumberTheWayPeopleTypeIt(string typed, double expected)
    {
        Assert.True(In("NO").TryParseNumber(typed.Replace(' ', Nbsp), out var value), typed);
        Assert.Equal((decimal)expected, value);
        Assert.True(In("NO").TryParseNumber(typed, out var withSpace), typed);
        Assert.Equal((decimal)expected, withSpace);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1,2,3")]
    [InlineData("12,3,4")]
    [InlineData("1,2.3,4")]
    [InlineData("--5")]
    [InlineData("5%")]
    public void ANumberThatIsNotOne_IsNotRead(string typed) => Assert.False(In("NO").TryParseNumber(typed, out _), typed);

    [Fact]
    public void ThePointIsTheDecimalPoint_WhereTheRegionSaysSo()
    {
        Assert.True(In("GB").TryParseNumber("1,234.56", out var gb));
        Assert.Equal(1234.56m, gb);
        Assert.True(In("GB").TryParseNumber("1,234", out var group));
        Assert.Equal(1234m, group); // three digits after the only comma: a group in this region
        Assert.True(In("US").TryParseNumber("0.5", out var us));
        Assert.Equal(0.5m, us);
    }

    // ------------------------------------------------------------------ nothing unreadable goes to the API

    [Fact]
    public void ADateOrNumberThatCouldNotBeRead_IsRefusedBeforeItIsSent()
    {
        ClientFieldDefinition Field(ClientFieldType type) => new() { Name = "f", Label = "f", Type = type };
        string? Check(ClientFieldType type, object? value) => ClientFieldValidator.ValidateField(
            Field(type), "owner", new Dictionary<string, object?> { ["f"] = value }, new I18nService());

        Assert.Null(Check(ClientFieldType.Date, "2026-12-31"));
        Assert.NotNull(Check(ClientFieldType.Date, "31.12.2026x"));
        Assert.NotNull(Check(ClientFieldType.Date, "abc"));
        Assert.Null(Check(ClientFieldType.Decimal, 1234.56m));
        Assert.Null(Check(ClientFieldType.Decimal, "1234.56"));
        Assert.NotNull(Check(ClientFieldType.Decimal, "1 234,5x"));
        Assert.NotNull(Check(ClientFieldType.Integer, "abc"));
    }

    // ------------------------------------------------------------------ the choice is kept by the server

    [Fact]
    public async Task AUserChoosesLanguageAndRegion_TheTenantHasDefaults_AndTheApiKeepsIsoAndPoint()
    {
        await using var factory = CreateFactory();
        using var admin = await LoginAsync(factory, null);
        var subject = $"region-{Guid.NewGuid():N}"[..16];
        try
        {
            // Nothing chosen: the tenant's defaults apply, and the session says so
            var start = await admin.GetFromJsonAsync<JsonElement>("/api/v1/me/preferences");
            Assert.Equal("nb", start.GetProperty("effectiveLanguage").GetString());
            Assert.Equal("NO", start.GetProperty("effectiveRegion").GetString());
            Assert.Equal(JsonValueKind.Null, start.GetProperty("language").ValueKind);

            // The user chooses English and Great Britain; the choice is kept and shows in the session
            using (var set = await admin.PutAsJsonAsync("/api/v1/me/preferences", new { language = "en", region = "gb" }))
            {
                Assert.Equal(HttpStatusCode.OK, set.StatusCode);
                var saved = await set.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("GB", saved.GetProperty("region").GetString());
            }

            var session = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/session")).GetProperty("user");
            Assert.Equal("en", session.GetProperty("effectiveLanguage").GetString());
            Assert.Equal("GB", session.GetProperty("effectiveRegion").GetString());

            // Not a language we have, and not a country code
            foreach (var bad in new object[] { new { language = "fr", region = "NO" }, new { language = "nb", region = "Norge" } })
            {
                using var refused = await admin.PutAsJsonAsync("/api/v1/me/preferences", bad);
                Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            }

            // Another user in the same tenant has made no choice and is not affected by it
            using (var created = await admin.PostAsJsonAsync("/api/v1/users", new { externalSubject = subject, email = $"{subject}@demohandel.no", displayName = "Region Test" }))
            {
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            }

            using var other = await LoginAsync(factory, subject);
            var otherPrefs = await other.GetFromJsonAsync<JsonElement>("/api/v1/me/preferences");
            Assert.Equal("nb", otherPrefs.GetProperty("effectiveLanguage").GetString());
            Assert.Equal("NO", otherPrefs.GetProperty("effectiveRegion").GetString());

            // The administrator changes the default; the one who has not chosen follows it, the one who has chosen does not
            using (var tenant = await admin.PutAsJsonAsync("/api/v1/tenant/preferences", new { defaultLanguage = "en", defaultRegion = "US" }))
            {
                Assert.Equal(HttpStatusCode.OK, tenant.StatusCode);
            }

            var followed = await other.GetFromJsonAsync<JsonElement>("/api/v1/me/preferences");
            Assert.Equal("en", followed.GetProperty("effectiveLanguage").GetString());
            Assert.Equal("US", followed.GetProperty("effectiveRegion").GetString());
            Assert.Equal("GB", (await admin.GetFromJsonAsync<JsonElement>("/api/v1/me/preferences")).GetProperty("effectiveRegion").GetString());

            // Only an administrator sets the default
            using (var denied = await other.PutAsJsonAsync("/api/v1/tenant/preferences", new { defaultLanguage = "nb", defaultRegion = "NO" }))
            {
                Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            }

            // Clearing the choice goes back to the default
            using (var clear = await admin.PutAsJsonAsync("/api/v1/me/preferences", new { language = (string?)null, region = "" }))
            {
                Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
            }

            Assert.Equal("US", (await admin.GetFromJsonAsync<JsonElement>("/api/v1/me/preferences")).GetProperty("effectiveRegion").GetString());

            // Whatever the user's region, the API stores and returns ISO dates and numbers with a decimal point,
            // and refuses a date written the Norwegian way
            using (var install = await admin.PostAsync("/api/v1/apps/install-builtin/core.currencies", null))
            {
                Assert.True(install.IsSuccessStatusCode, await install.Content.ReadAsStringAsync());
            }

            try
            {
                var url = $"/api/v1/{DemoTenantSeeder.DemoCompanyId}/core.exchangerate";
                using (var norwegian = await admin.PostAsJsonAsync(url, new { fromCurrency = "EUR", toCurrency = "NOK", rateDate = "31.12.2026", rate = 11.5432 }))
                {
                    Assert.Equal(HttpStatusCode.BadRequest, norwegian.StatusCode);
                }

                using var createdRate = await admin.PostAsJsonAsync(url, new { fromCurrency = "EUR", toCurrency = "NOK", rateDate = "2026-12-31", rate = 11.5432 });
                Assert.Equal(HttpStatusCode.Created, createdRate.StatusCode);
                var id = JsonDocument.Parse(await createdRate.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
                var raw = await admin.GetStringAsync($"{url}/{id}");
                using var read = JsonDocument.Parse(raw);
                var fields = read.RootElement.GetProperty("fields");
                Assert.Equal("2026-12-31", fields.GetProperty("rateDate").GetString());
                Assert.Equal(JsonValueKind.Number, fields.GetProperty("rate").ValueKind);
                Assert.Contains("11.5432", fields.GetProperty("rate").GetRawText());
                Assert.DoesNotContain("11,5432", raw);
            }
            finally
            {
                using var uninstall = await admin.DeleteAsync("/api/v1/apps/core.currencies?purge=true");
            }
        }
        finally
        {
            await using var migrator = new NpgsqlConnection(fixture.MigrationConnectionString);
            await migrator.OpenAsync();
            await using var reset = new NpgsqlCommand(
                """
                UPDATE core.tenant SET default_language = 'nb', default_country_code = 'NO' WHERE id = @tenant;
                UPDATE core."user" SET language = NULL, region = NULL WHERE tenant_id = @tenant;
                DELETE FROM core."user" WHERE external_subject = @subject;
                """, migrator);
            reset.Parameters.AddWithValue("tenant", DemoTenantSeeder.DemoTenantId);
            reset.Parameters.AddWithValue("subject", subject);
            await reset.ExecuteNonQueryAsync();
        }
    }

    private static async Task<HttpClient> LoginAsync(WebApplicationFactory<Program> factory, string? subject)
    {
        var client = factory.CreateClient();
        using var res = await client.PostAsJsonAsync("/api/v1/auth/dev-login", new { subject });
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
                    ["Email:SmtpHost"] = ""
                });
            });
        });
    }
}
