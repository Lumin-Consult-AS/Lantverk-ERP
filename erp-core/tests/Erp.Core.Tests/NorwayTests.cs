using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Erp.Core.Domain.Tenancy;
using Erp.Core.Infrastructure.Apps;
using Erp.Core.Infrastructure.Persistence;
using Erp.Sdk;
using Finance;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Norway;
using Npgsql;
using Xunit;

namespace Erp.Core.Tests;

/// <summary>The Norway package (<c>norway</c>): chart of accounts, VAT codes, VAT on voucher lines, VAT entries and the VAT return.</summary>
[Collection(PostgresDatabaseCollection.Name)]
public sealed class NorwayTests(PostgresDatabaseFixture fixture)
{
    private const string TestOidcSigningKey = "test-only-external-oidc-signing-key-at-least-32-bytes!";
    private const string TestOidcIssuer = "https://oidc.test.local/realms/erp-dev";
    private const string TestOidcAudience = "erp-web";

    private sealed record Env(
        WebApplicationFactory<Program> Factory, WebApplication Finance, WebApplication Norway, HttpClient Client, string MigrationConnectionString)
        : IAsyncDisposable
    {
        private static readonly string[] FinanceTables =
            ["settlementitem", "settlement", "openitem", "posting", "journalentry", "voucherline", "voucher", "account"];

        public async ValueTask DisposeAsync()
        {
            using (var uninstall = await Client.DeleteAsync("/api/v1/apps/norway?purge=true"))
            {
                Assert.True(uninstall.IsSuccessStatusCode, await uninstall.Content.ReadAsStringAsync());
            }

            using (var uninstall = await Client.DeleteAsync("/api/v1/apps/finance"))
            {
                Assert.True(uninstall.IsSuccessStatusCode, await uninstall.Content.ReadAsStringAsync());
            }

            await using (var conn = new NpgsqlConnection(MigrationConnectionString))
            {
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand(
                    "SET session_replication_role = replica;\n"
                    + string.Join("\n", FinanceTables.SelectMany(t => new[] { $"DELETE FROM finance.{t};", $"DELETE FROM finance.{t}_audit;" }))
                    + "\nDELETE FROM core.number_counter WHERE object_id LIKE 'finance.%';",
                    conn);
                await cmd.ExecuteNonQueryAsync();
            }

            var archives = await Client.GetFromJsonAsync<JsonElement>("/api/v1/apps/archives");
            foreach (var archive in archives.EnumerateArray().Where(a => a.GetProperty("appId").GetString() is "finance" or "norway"))
            {
                await Client.PostAsync($"/api/v1/apps/archives/{archive.GetProperty("id").GetGuid()}/purge", null);
            }

            foreach (var app in new[] { "core.currencies", "core.organization", "core.dimensions", "core.parties" })
            {
                await Client.DeleteAsync($"/api/v1/apps/{app}?purge=true");
            }

            Client.Dispose();
            await Norway.DisposeAsync();
            await Finance.DisposeAsync();
            await Factory.DisposeAsync();
        }
    }

    // ------------------------------------------------------------------ set-up

    [Fact]
    public async Task TheFirstFiscalYear_BringsTheStandardChartOfAccountsAndVatCodes_AndASecondRunAddsNothing()
    {
        await using var env = await SetUpAsync();
        var c = env.Client;

        var accounts = await QueryAsync(c, "finance.account", "number ge '0'", top: 500);
        Assert.True(accounts.Count > 60);
        var vatAccount = accounts.Single(a => Field(a, "number") == "2700");
        Assert.Equal("liability", Field(vatAccount, "accountType"));
        Assert.Equal("2700", Field(vatAccount, "norway.standardAccount"));
        Assert.Equal("receivable", Field(accounts.Single(a => Field(a, "number") == "1500"), "controlType"));
        Assert.Equal("payable", Field(accounts.Single(a => Field(a, "number") == "2400"), "controlType"));

        var codes = await QueryAsync(c, "norway.vatcode", "code ne ''", top: 100);
        Assert.Equal(12, codes.Count);
        var high = codes.Single(x => Field(x, "code") == "3");
        Assert.Equal("output", Field(high, "direction"));
        Assert.Equal(Id(vatAccount), Guid.Parse(Field(high, "account")!));
        var highRate = Assert.Single(await QueryAsync(c, "norway.vatrate", $"vatCode eq '{Id(high)}'"));
        Assert.Equal(25m, Decimal(highRate, "rate"));

        // The record of the version stops a second run: what the company changed or deleted is not put back
        var account = accounts.Single(a => Field(a, "number") == "6900");
        using (var rename = await PatchAsync(c, "finance.account", account, new { name = "Mobil og bredbånd" }))
        {
            Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
        }

        await CreateAsync(c, "core.fiscalyear", new { year = 2027, startDate = "2027-01-01", endDate = "2027-12-31" });
        await Task.Delay(1500);
        Assert.Equal(accounts.Count, (await QueryAsync(c, "finance.account", "number ge '0'", top: 500)).Count);
        Assert.Equal("Mobil og bredbånd", Field((await QueryAsync(c, "finance.account", "number eq '6900'")).Single(), "name"));
    }

    [Fact]
    public async Task TheChartOfAccounts_CanBeExtendedChangedAndPruned_AndAnAccountInUseCannotBeRemoved()
    {
        await using var env = await SetUpAsync();
        var c = env.Client;
        var a = await AccountsAsync(c);

        // Add an account of your own, with a SAF-T standard account, and change it
        var own = await CreateAsync(c, "finance.account", new Dictionary<string, object?>
        {
            ["number"] = "1925", ["name"] = "Sparekonto", ["accountType"] = "asset", ["norway.standardAccount"] = "1920"
        });
        using (var change = await PatchAsync(c, "finance.account", own, new Dictionary<string, object?>
               {
                   ["name"] = "Sparekonto skatt", ["norway.standardAccount"] = "1950"
               }))
        {
            Assert.Equal(HttpStatusCode.OK, change.StatusCode);
            own = JsonDocument.Parse(await change.Content.ReadAsStringAsync()).RootElement.Clone();
        }

        Assert.Equal("Sparekonto skatt", Field(own, "name"));
        Assert.Equal("1950", Field(own, "norway.standardAccount"));

        // An account that is not used can be removed, also one from the standard chart
        Assert.True(await DeleteAsync(c, "finance.account", own));
        Assert.True(await DeleteAsync(c, "finance.account", a["6940"]));
        Assert.Empty(await QueryAsync(c, "finance.account", "number eq '6940'"));

        // An account that has been posted to cannot be removed (the ledger is immutable); it can be made inactive instead
        await PostAsync(c, "2026-03-05", "Salg", (a["1920"], 100m, 0m, null), (a["3000"], 0m, 100m, null));
        Assert.False(await DeleteAsync(c, "finance.account", a["3000"]));
        using (var deactivate = await PatchAsync(c, "finance.account", a["3000"], new { isActive = false }))
        {
            Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);
        }

        // The VAT account of a VAT code can be removed, but then the code is refused on a line until it has an account again
        Assert.True(await DeleteAsync(c, "finance.account", a["2700"]));
        var draft = await CreateVoucherAsync(c, "2026-03-06", "Uten MVA-konto");
        using (var noAccount = await c.PostAsJsonAsync(Url("finance.voucherline"), new Dictionary<string, object?>
               {
                   ["voucher"] = Id(draft), ["account"] = Id(a["3100"]), ["credit"] = 100m, ["norway.vatCode"] = Id(a.Code("3"))
               }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, noAccount.StatusCode);
            Assert.Contains("mangler MVA-konto", await noAccount.Content.ReadAsStringAsync());
        }

        // What was removed or changed is not put back by the set-up
        await CreateAsync(c, "core.fiscalyear", new { year = 2027, startDate = "2027-01-01", endDate = "2027-12-31" });
        await Task.Delay(1500);
        Assert.Empty(await QueryAsync(c, "finance.account", "number eq '6940'"));
    }

    // ------------------------------------------------------------------ VAT on lines

    [Fact]
    public async Task VatIsCalculatedOnLines_FollowsTheBaseLine_AndAHandEditedVatLineIsRefusedAtPosting()
    {
        await using var env = await SetUpAsync();
        var c = env.Client;
        var a = await AccountsAsync(c);

        // Sale 1000.00 net, high rate: 250.00 output VAT on 2700 (credit), bank 1250.00
        var sale = await CreateVoucherAsync(c, "2026-03-10", "Salg");
        await AddLineAsync(c, sale, a["1920"], debit: 1250m);
        var salesLine = await AddLineAsync(c, sale, a["3000"], credit: 1000m, vatCode: a.Code("3"));
        var vatLine = Assert.Single(await QueryAsync(c, "finance.voucherline", $"voucher eq '{Id(sale)}' and credit gt 0 and debit eq 0 and description eq 'MVA 3'"));
        Assert.Equal(250m, Decimal(vatLine, "credit"));
        Assert.Equal(Id(a["2700"]), Guid.Parse(Field(vatLine, "account")!));
        Assert.Equal(Id(salesLine), Guid.Parse(Field(vatLine, "norway.vatSourceLine")!));

        // The VAT line follows the base line when it changes
        using (var change = await PatchAsync(c, "finance.voucherline", salesLine, new { credit = 800m }))
        {
            Assert.Equal(HttpStatusCode.OK, change.StatusCode);
        }

        vatLine = Assert.Single(await QueryAsync(c, "finance.voucherline", $"voucher eq '{Id(sale)}' and description eq 'MVA 3'"));
        Assert.Equal(200m, Decimal(vatLine, "credit"));

        // A hand-edited VAT line is refused when posting (the bank line is brought in step first)
        using (var tamper = await PatchAsync(c, "finance.voucherline", vatLine, new { credit = 1m }))
        {
            Assert.Equal(HttpStatusCode.OK, tamper.StatusCode);
        }

        var bank = (await QueryAsync(c, "finance.voucherline", $"voucher eq '{Id(sale)}' and debit gt 0")).Single();
        using (var bankFix = await PatchAsync(c, "finance.voucherline", bank, new { debit = 801m }))
        {
            Assert.Equal(HttpStatusCode.OK, bankFix.StatusCode);
        }

        using (var refused = await c.PostAsync($"{Url("finance.voucher")}/{Id(sale)}/transitions/post", null))
        {
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("stemmer ikke med grunnlaget", await refused.Content.ReadAsStringAsync());
        }

        // Saving the base line again puts the VAT line right; deleting the base line removes the VAT line
        var refreshed = (await QueryAsync(c, "finance.voucherline", $"voucher eq '{Id(sale)}' and credit eq 800")).Single();
        using (var touch = await PatchAsync(c, "finance.voucherline", refreshed, new { description = "Varer" }))
        {
            Assert.Equal(HttpStatusCode.OK, touch.StatusCode);
        }

        Assert.Equal(200m, Decimal(Assert.Single(await QueryAsync(c, "finance.voucherline", $"voucher eq '{Id(sale)}' and description eq 'MVA 3'")), "credit"));
        using (var remove = new HttpRequestMessage(HttpMethod.Delete, $"{Url("finance.voucherline")}/{Id(refreshed)}"))
        {
            var current = await c.GetFromJsonAsync<JsonElement>($"{Url("finance.voucherline")}/{Id(refreshed)}");
            remove.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{current.GetProperty("rowVersion").GetInt64()}\""));
            using var res = await c.SendAsync(remove);
            Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        }

        Assert.Empty(await QueryAsync(c, "finance.voucherline", $"voucher eq '{Id(sale)}' and description eq 'MVA 3'"));

        // A code with no rate on the voucher date is refused on the line, and a voucher needs a text
        var old = await CreateVoucherAsync(c, "2021-06-01", "Gammelt");
        using (var noRate = await c.PostAsJsonAsync(Url("finance.voucherline"), new Dictionary<string, object?>
               {
                   ["voucher"] = Id(old), ["account"] = Id(a["3000"]), ["credit"] = 100m, ["norway.vatCode"] = Id(a.Code("3"))
               }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, noRate.StatusCode);
            Assert.Contains("ingen sats", await noRate.Content.ReadAsStringAsync());
        }

        var silent = await CreateVoucherAsync(c, "2026-03-11", null);
        await AddLineAsync(c, silent, a["1920"], debit: 10m);
        await AddLineAsync(c, silent, a["3000"], credit: 10m);
        using (var noText = await c.PostAsync($"{Url("finance.voucher")}/{Id(silent)}/transitions/post", null))
        {
            Assert.Equal(HttpStatusCode.BadRequest, noText.StatusCode);
            Assert.Contains("tekst", await noText.Content.ReadAsStringAsync());
        }
    }

    // ------------------------------------------------------------------ the VAT return

    [Fact]
    public async Task TheVatReturn_AddsUpBasisAndVatPerCode_AndAReversalCancelsIt()
    {
        await using var env = await SetUpAsync();
        var c = env.Client;
        var a = await AccountsAsync(c);

        // Sales: 1000.00 at 25% (250.00); 500.00 at 12% (60.00); 333.33 at 15% (49.9995 rounds to 50.00); export 2000.00 at 0%
        var s1 = await PostAsync(c, "2026-03-05", "Salg høy sats", (a["1920"], 1250m, 0m, null), (a["3000"], 0m, 1000m, a.Code("3")));
        await PostAsync(c, "2026-03-06", "Salg lav sats", (a["1920"], 560m, 0m, null), (a["3000"], 0m, 500m, a.Code("33")));
        await PostAsync(c, "2026-03-07", "Salg middels sats", (a["1920"], 383.33m, 0m, null), (a["3000"], 0m, 333.33m, a.Code("31")));
        await PostAsync(c, "2026-03-08", "Eksport", (a["1920"], 2000m, 0m, null), (a["3000"], 0m, 2000m, a.Code("52")));

        // Purchases: 800.00 at 25% deductible (200.00); 90.00 without VAT treatment (code 7)
        await PostAsync(c, "2026-03-12", "Leie", (a["6300"], 800m, 0m, a.Code("1")), (a["1920"], 0m, 1000m, null));
        await PostAsync(c, "2026-03-13", "Gebyr", (a["7770"], 90m, 0m, a.Code("7")), (a["1920"], 0m, 90m, null));

        // Outside the term
        await PostAsync(c, "2026-05-02", "Mai", (a["1920"], 125m, 0m, null), (a["3000"], 0m, 100m, a.Code("3")));

        var vatReturn = await CreateAsync(c, "norway.vatreturn", new { name = "2026 termin 2", startDate = "2026-03-01", endDate = "2026-04-30" });
        var calculated = await TransitionAsync(c, "norway.vatreturn", vatReturn, "calculate");
        Assert.Equal("calculated", Field(calculated, "status"));

        var lines = await WaitForAsync(async () =>
        {
            var found = await QueryAsync(c, "norway.vatreturnline", $"vatReturn eq '{Id(vatReturn)}'");
            return found.Count == 6 ? found : null;
        });
        decimal Basis(string code) => Decimal(lines.Single(l => Guid.Parse(Field(l, "vatCode")!) == Id(a.Code(code))), "basis");
        decimal Vat(string code) => Decimal(lines.Single(l => Guid.Parse(Field(l, "vatCode")!) == Id(a.Code(code))), "vatAmount");
        Assert.Equal((1000m, 250m), (Basis("3"), Vat("3")));
        Assert.Equal((500m, 60m), (Basis("33"), Vat("33")));
        Assert.Equal((333.33m, 50m), (Basis("31"), Vat("31")));
        Assert.Equal((2000m, 0m), (Basis("52"), Vat("52")));
        Assert.Equal((800m, 200m), (Basis("1"), Vat("1")));
        Assert.Equal((90m, 0m), (Basis("7"), Vat("7")));

        var result = await c.GetFromJsonAsync<JsonElement>($"{Url("norway.vatreturn")}/{Id(vatReturn)}");
        Assert.Equal(360m, Decimal(result, "outputVat"));
        Assert.Equal(200m, Decimal(result, "inputVat"));
        Assert.Equal(160m, Decimal(result, "payable"));

        // The VAT account in the ledger agrees with the return (March + May: 250 + 60 + 50 + 25 output, 200 input)
        var ledger = await AggregateAsync(c, "finance.posting", new
        {
            groupBy = new[] { "account" },
            measures = new object[] { new { function = "sum", field = "credit", @as = "credit" }, new { function = "sum", field = "debit", @as = "debit" } }
        });
        decimal Sum(string accountKey, string measure) =>
            ledger.Where(r => Guid.Parse(r.GetProperty("account").GetString()!) == Id(a[accountKey])).Sum(r => r.GetProperty(measure).GetDecimal());
        Assert.Equal(385m, Sum("2700", "credit"));
        Assert.Equal(200m, Sum("2710", "debit"));

        // Reopen removes the lines; a reversal of a sale is negated in the entries, and the term then adds up without it
        var reopened = await TransitionAsync(c, "norway.vatreturn", result, "reopen");
        Assert.Equal("draft", Field(reopened, "status"));
        Assert.Empty(await QueryAsync(c, "norway.vatreturnline", $"vatReturn eq '{Id(vatReturn)}'"));

        var entry = (await QueryAsync(c, "finance.journalentry", $"voucher eq '{s1}'")).Single();
        var reversal = await CreateAsync(c, "finance.voucher", new Dictionary<string, object?>
        {
            ["voucherDate"] = "2026-03-20", ["description"] = "Kreditnota", ["reverses"] = Id(entry)
        });
        await WaitForAsync(async () => (await QueryAsync(c, "finance.voucherline", $"voucher eq '{Id(reversal)}'")).Count == 3 ? [] : null);
        await TransitionAsync(c, "finance.voucher", reversal, "post");
        await WaitForAsync(async () =>
        {
            var found = await QueryAsync(c, "norway.vatentry", "sourceKey ne ''", top: 100);
            return found.Any(e => Field(e, "sourceKey")!.StartsWith("rev:")) ? found : null;
        });

        var again = await TransitionAsync(c, "norway.vatreturn", reopened, "calculate");
        Assert.Equal("calculated", Field(again, "status"));
        var secondLines = await WaitForAsync(async () =>
        {
            var found = await QueryAsync(c, "norway.vatreturnline", $"vatReturn eq '{Id(vatReturn)}'");
            return found.Count == 6 ? found : null;
        });
        Assert.Equal(0m, Decimal(secondLines.Single(l => Guid.Parse(Field(l, "vatCode")!) == Id(a.Code("3"))), "vatAmount"));
        var secondResult = await c.GetFromJsonAsync<JsonElement>($"{Url("norway.vatreturn")}/{Id(vatReturn)}");
        Assert.Equal(110m, Decimal(secondResult, "outputVat"));
        Assert.Equal(-90m, Decimal(secondResult, "payable"));
    }

    // ------------------------------------------------------------------ separation

    [Fact]
    public void TheGeneralLedger_HasNoNorwegianValues_AndDoesNotKnowThePackage()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "erp-apps", "finance")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var forbidden = new System.Text.RegularExpressions.Regex(
            @"\b(NOK|MVA|SAF-?T|NS ?4102|norway|norsk|Skatteetaten|2700|2710)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var files = Directory.EnumerateFiles(Path.Combine(dir!.FullName, "erp-apps", "finance"), "*.*", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && Path.GetExtension(f) is ".cs" or ".yaml");
        var hits = new List<string>();
        foreach (var file in files)
        {
            foreach (var (text, number) in File.ReadLines(file).Select((l, i) => (l.Trim(), i + 1)))
            {
                // Comments may point to the package; code and data may not use it
                if (!text.StartsWith("//") && !text.StartsWith('#') && forbidden.IsMatch(text))
                {
                    hits.Add($"{Path.GetFileName(file)}:{number}: {text}");
                }
            }
        }

        Assert.Empty(hits);
    }

    // ------------------------------------------------------------------ helpers

    private sealed class Accounts : Dictionary<string, JsonElement>
    {
        public Dictionary<string, JsonElement> Codes { get; } = [];

        public JsonElement Code(string code) => Codes[code];
    }

    private static async Task<Accounts> AccountsAsync(HttpClient c)
    {
        var result = new Accounts();
        foreach (var account in await QueryAsync(c, "finance.account", "number ge '0'", top: 500))
        {
            result[Field(account, "number")!] = account;
        }

        foreach (var code in await QueryAsync(c, "norway.vatcode", "code ne ''", top: 100))
        {
            result.Codes[Field(code, "code")!] = code;
        }

        return result;
    }

    private static async Task<Guid> PostAsync(
        HttpClient c, string date, string text, params (JsonElement Account, decimal Debit, decimal Credit, JsonElement? Code)[] lines)
    {
        var voucher = await CreateVoucherAsync(c, date, text);
        foreach (var (account, debit, credit, code) in lines)
        {
            await AddLineAsync(c, voucher, account, debit, credit, code);
        }

        await TransitionAsync(c, "finance.voucher", voucher, "post");
        var entry = (await QueryAsync(c, "finance.journalentry", $"voucher eq '{Id(voucher)}'")).Single();
        await WaitForAsync(async () => (await QueryAsync(c, "norway.vatentry", $"entry eq '{Id(entry)}'")).Count >= lines.Count(l => l.Code is not null) ? [] : null);
        return Id(voucher);
    }

    private async Task<Env> SetUpAsync()
    {
        var factory = CreateFactory();
        var finance = await StartContainerAsync<FinanceApp>(factory);
        var norway = await StartContainerAsync<NorwayApp>(factory);
        var orchestrator = factory.Services.GetRequiredService<KubernetesAppOrchestrator>();
        orchestrator.RegisterEndpoint("finance", "1.0.0", "http://finance:50051", () => finance.GetTestServer().CreateHandler());
        orchestrator.RegisterEndpoint("norway", "1.0.0", "http://norway:50051", () => norway.GetTestServer().CreateHandler());

        var client = factory.CreateClient();
        using (var res = await client.PostAsJsonAsync("/api/v1/auth/dev-login", new { }))
        {
            res.EnsureSuccessStatusCode();
            var token = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("accessToken").GetString();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var env = new Env(factory, finance, norway, client, fixture.MigrationConnectionString);
        foreach (var app in new[] { "core.parties", "core.dimensions", "core.organization", "core.currencies", "finance", "norway" })
        {
            using var res = await client.PostAsync($"/api/v1/apps/install-builtin/{app}", null);
            Assert.True(res.IsSuccessStatusCode, $"{app}: {await res.Content.ReadAsStringAsync()}");
        }

        var year = await CreateAsync(client, "core.fiscalyear", new { year = 2026, startDate = "2026-01-01", endDate = "2026-12-31" });
        await WaitForAsync(async () =>
        {
            var periods = await QueryAsync(client, "core.fiscalperiod", $"fiscalYear eq '{Id(year)}'", top: 50);
            var settings = await QueryAsync(client, "norway.settings", "singleton eq 'settings'");
            return periods.Count == 12 && settings.Count == 1 && Field(settings[0], "chartVersion") == NorwayApp.ChartVersion ? periods : null;
        });
        return env;
    }

    private static Task<JsonElement> CreateVoucherAsync(HttpClient c, string date, string? text) =>
        CreateAsync(c, "finance.voucher", new Dictionary<string, object?> { ["voucherDate"] = date, ["description"] = text });

    private static Task<JsonElement> AddLineAsync(
        HttpClient c, JsonElement voucher, JsonElement account, decimal debit = 0, decimal credit = 0, JsonElement? vatCode = null) =>
        CreateAsync(c, "finance.voucherline", new Dictionary<string, object?>
        {
            ["voucher"] = Id(voucher),
            ["account"] = Id(account),
            ["debit"] = debit,
            ["credit"] = credit,
            ["norway.vatCode"] = vatCode is { } code ? Id(code) : null
        });

    private static string Url(string objectId) => $"/api/v1/{DemoTenantSeeder.DemoCompanyId}/{objectId}";

    private static Guid Id(JsonElement record) => record.GetProperty("id").GetGuid();

    private static string? Field(JsonElement record, string name) =>
        record.GetProperty("fields").TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : null;

    private static decimal Decimal(JsonElement record, string name) => record.GetProperty("fields").GetProperty(name).GetDecimal();

    private static async Task<JsonElement> CreateAsync(HttpClient c, string objectId, object body)
    {
        using var res = await c.PostAsJsonAsync(Url(objectId), body);
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"create {objectId}: {(int)res.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<List<JsonElement>> QueryAsync(HttpClient c, string objectId, string filter, int top = 200)
    {
        var res = await c.GetFromJsonAsync<JsonElement>($"{Url(objectId)}?filter={Uri.EscapeDataString(filter)}&top={top}");
        return res.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private static async Task<bool> DeleteAsync(HttpClient c, string objectId, JsonElement record)
    {
        var current = await c.GetFromJsonAsync<JsonElement>($"{Url(objectId)}/{Id(record)}");
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"{Url(objectId)}/{Id(record)}");
        req.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{current.GetProperty("rowVersion").GetInt64()}\""));
        using var res = await c.SendAsync(req);
        return res.IsSuccessStatusCode;
    }

    private static async Task<List<JsonElement>> AggregateAsync(HttpClient c, string objectId, object body)
    {
        using var res = await c.PostAsJsonAsync($"{Url(objectId)}/aggregate", body);
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, text);
        return JsonDocument.Parse(text).RootElement.GetProperty("rows").EnumerateArray().Select(r => r.Clone()).ToList();
    }

    private static async Task<JsonElement> TransitionAsync(HttpClient c, string objectId, JsonElement record, string transition)
    {
        using var res = await c.PostAsync($"{Url(objectId)}/{Id(record)}/transitions/{transition}", null);
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{objectId} {transition}: {(int)res.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<HttpResponseMessage> PatchAsync(HttpClient c, string objectId, JsonElement record, object body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Patch, $"{Url(objectId)}/{Id(record)}");
        req.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{record.GetProperty("rowVersion").GetInt64()}\""));
        req.Content = JsonContent.Create(body);
        return await c.SendAsync(req);
    }

    private static async Task<List<JsonElement>> WaitForAsync(Func<Task<List<JsonElement>?>> probe)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (await probe() is { } result)
            {
                return result;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException("The expected data did not appear (event delivery).");
    }

    private static async Task<WebApplication> StartContainerAsync<TApp>(WebApplicationFactory<Program> core) where TApp : AppBase
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddErpApp<TApp>(opts =>
        {
            opts.CoreGrpcAddress = "http://erp-core-loopback";
            opts.ChannelHandlerFactory = () => core.Server.CreateHandler();
        });

        var app = builder.Build();
        app.MapErpApp<TApp>();
        await app.StartAsync();
        return app;
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
