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
using Npgsql;
using Xunit;

namespace Erp.Core.Tests;

/// <summary>The general ledger (<c>finance</c>): posting, balance, periods, immutability, reversal and settlement.</summary>
[Collection(PostgresDatabaseCollection.Name)]
public sealed class FinanceTests(PostgresDatabaseFixture fixture)
{
    private const string TestOidcSigningKey = "test-only-external-oidc-signing-key-at-least-32-bytes!";
    private const string TestOidcIssuer = "https://oidc.test.local/realms/erp-dev";
    private const string TestOidcAudience = "erp-web";

    private sealed record Env(WebApplicationFactory<Program> Factory, WebApplication Host, HttpClient Client, string MigrationConnectionString)
        : IAsyncDisposable
    {
        private static readonly string[] FinanceTables =
            ["settlementitem", "settlement", "openitem", "posting", "journalentry", "voucherline", "voucher", "account"];

        public async ValueTask DisposeAsync()
        {
            // Posted records cannot be deleted through the application (that is the point), so a test cleans up as the
            // database owner: uninstall (archive), empty the tables with triggers off, then let the archive purge drop them.
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
            foreach (var archive in archives.EnumerateArray().Where(a => a.GetProperty("appId").GetString() == "finance"))
            {
                await Client.PostAsync($"/api/v1/apps/archives/{archive.GetProperty("id").GetGuid()}/purge", null);
            }

            foreach (var app in new[] { "core.organization", "core.dimensions", "core.parties" })
            {
                await Client.DeleteAsync($"/api/v1/apps/{app}?purge=true");
            }

            Client.Dispose();
            await Host.DisposeAsync();
            await Factory.DisposeAsync();
        }
    }

    private sealed record Chart(JsonElement Bank, JsonElement Receivables, JsonElement Payables, JsonElement Sales, JsonElement Rent, JsonElement Dormant);

    // ------------------------------------------------------------------ posting

    [Fact]
    public async Task APostedVoucher_BecomesAnImmutableEntryWithPostingsAndOpenItems_AndTheTrialBalanceBalances()
    {
        await using var env = await SetUpAsync();
        var c = env.Client;
        var chart = await CreateChartAsync(c);
        var customer = await CreatePartyAsync(c, "K-1");

        var voucher = await CreateVoucherAsync(c, "2026-03-15", "Salg til kunde");
        await AddLineAsync(c, voucher, chart.Receivables, debit: 1250m, party: Id(customer), dueDate: "2026-04-14");
        await AddLineAsync(c, voucher, chart.Sales, credit: 1250m);

        var posted = await TransitionAsync(c, "finance.voucher", voucher, "post");
        Assert.Equal("posted", Field(posted, "status"));

        // One entry with a gap-free number, two postings, one open item
        var entries = await QueryAsync(c, "finance.journalentry", $"voucher eq '{Id(voucher)}'");
        var entry = Assert.Single(entries);
        Assert.Matches(@"^2026-\d{6}$", Field(entry, "entryNumber")!);
        var postings = await QueryAsync(c, "finance.posting", $"entry eq '{Id(entry)}'");
        Assert.Equal(2, postings.Count);
        Assert.Equal(1250m, postings.Sum(p => Decimal(p, "debit")));
        Assert.Equal(1250m, postings.Sum(p => Decimal(p, "credit")));
        var open = Assert.Single(await QueryAsync(c, "finance.openitem", $"party eq '{Id(customer)}'"));
        Assert.Equal(1250m, Decimal(open, "amount"));
        Assert.Equal(1250m, Decimal(open, "remaining"));
        Assert.Equal("2026-04-14", Field(open, "dueDate"));

        // The voucher, its lines and the postings cannot be changed or deleted, by the API or directly in the database
        using (var edit = await PatchAsync(c, "finance.voucher", posted, new { description = "Endret" }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, edit.StatusCode);
        }

        var line = (await QueryAsync(c, "finance.voucherline", $"voucher eq '{Id(voucher)}'"))[0];
        Assert.Equal("posted", Field(line, "status"));
        using (var editLine = await PatchAsync(c, "finance.voucherline", line, new { debit = 1m }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, editLine.StatusCode);
        }

        using (var newLine = await c.PostAsJsonAsync(Url("finance.voucherline"),
                   new { voucher = Id(voucher), account = Id(chart.Bank), debit = 5m }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, newLine.StatusCode);
        }

        await AssertDatabaseRefusesAsync($"UPDATE finance.posting SET debit = 0 WHERE id = '{Id(postings[0])}';");
        await AssertDatabaseRefusesAsync($"DELETE FROM finance.journalentry WHERE id = '{Id(entry)}';");

        // Trial balance by account: the sums of debit and credit are equal
        var trial = await AggregateAsync(c, "finance.posting", new
        {
            groupBy = new[] { "account" },
            measures = new object[] { new { function = "sum", field = "debit", @as = "debit" }, new { function = "sum", field = "credit", @as = "credit" } }
        });
        Assert.Equal(trial.Sum(r => r.GetProperty("debit").GetDecimal()), trial.Sum(r => r.GetProperty("credit").GetDecimal()));
    }

    [Fact]
    public async Task APostIsRefused_WhenTheVoucherIsWrong_AndNothingIsLeftBehind_AndNumbersStayGapFree()
    {
        await using var env = await SetUpAsync();
        var c = env.Client;
        var chart = await CreateChartAsync(c);
        var customer = await CreatePartyAsync(c, "K-2");

        async Task<string> ExpectRefusedAsync(JsonElement voucher)
        {
            using var res = await c.PostAsync($"{Url("finance.voucher")}/{Id(voucher)}/transitions/post", null);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            Assert.Empty(await QueryAsync(c, "finance.journalentry", $"voucher eq '{Id(voucher)}'"));
            var again = await c.GetFromJsonAsync<JsonElement>($"{Url("finance.voucher")}/{Id(voucher)}");
            Assert.Equal("draft", Field(again, "status"));
            return await res.Content.ReadAsStringAsync();
        }

        // Does not balance
        var unbalanced = await CreateVoucherAsync(c, "2026-03-10", "Ubalansert");
        await AddLineAsync(c, unbalanced, chart.Bank, debit: 100m);
        await AddLineAsync(c, unbalanced, chart.Sales, credit: 90m);
        Assert.Contains("balanse", await ExpectRefusedAsync(unbalanced));

        // Only one line
        var single = await CreateVoucherAsync(c, "2026-03-10", "Én linje");
        await AddLineAsync(c, single, chart.Bank, debit: 100m);
        Assert.Contains("to linjer", await ExpectRefusedAsync(single));

        // A debit and a credit on the same line
        var both = await CreateVoucherAsync(c, "2026-03-10", "Begge sider");
        await AddLineAsync(c, both, chart.Bank, debit: 100m, credit: 100m);
        await AddLineAsync(c, both, chart.Sales, credit: 100m);
        Assert.Contains("debet eller kredit", await ExpectRefusedAsync(both));

        // A receivables line needs a party
        var noParty = await CreateVoucherAsync(c, "2026-03-10", "Uten part");
        await AddLineAsync(c, noParty, chart.Receivables, debit: 100m);
        await AddLineAsync(c, noParty, chart.Sales, credit: 100m);
        Assert.Contains("part", await ExpectRefusedAsync(noParty));

        // An inactive account
        var dormant = await CreateVoucherAsync(c, "2026-03-10", "Inaktiv konto");
        await AddLineAsync(c, dormant, chart.Dormant, debit: 100m);
        await AddLineAsync(c, dormant, chart.Sales, credit: 100m);
        Assert.Contains("inaktiv", await ExpectRefusedAsync(dormant));

        // Outside the fiscal year, no open period
        var outside = await CreateVoucherAsync(c, "2027-02-10", "Utenfor året");
        await AddLineAsync(c, outside, chart.Bank, debit: 100m);
        await AddLineAsync(c, outside, chart.Sales, credit: 100m);
        Assert.Contains("åpen regnskapsperiode", await ExpectRefusedAsync(outside));

        // A closed period
        var april = (await QueryAsync(c, "core.fiscalperiod", "startDate eq '2026-04-01'")).Single();
        await TransitionAsync(c, "core.fiscalperiod", april, "close");
        var closed = await CreateVoucherAsync(c, "2026-04-05", "Stengt periode");
        await AddLineAsync(c, closed, chart.Bank, debit: 100m);
        await AddLineAsync(c, closed, chart.Sales, credit: 100m);
        Assert.Contains("åpen regnskapsperiode", await ExpectRefusedAsync(closed));

        // The failures above took no numbers: two good vouchers get consecutive numbers
        var first = await PostSimpleAsync(c, chart, "2026-03-11");
        var second = await PostSimpleAsync(c, chart, "2026-03-12");
        Assert.Equal(Sequence(first) + 1, Sequence(second));

        // After reopening the period, the voucher that was refused can be posted
        await TransitionAsync(c, "core.fiscalperiod", april, "reopen");
        var posted = await TransitionAsync(c, "finance.voucher", closed, "post");
        Assert.Equal("posted", Field(posted, "status"));
    }

    [Fact]
    public async Task ACorrectionIsMadeWithAReversal_OnlyOncePerEntry_AndOpenItemsAreSettledAgainstEachOther()
    {
        await using var env = await SetUpAsync();
        var c = env.Client;
        var chart = await CreateChartAsync(c);
        var customer = await CreatePartyAsync(c, "K-3");

        // The original entry: receivable 800 / sales 800
        var original = await CreateVoucherAsync(c, "2026-05-02", "Feil faktura");
        await AddLineAsync(c, original, chart.Receivables, debit: 800m, party: Id(customer), dueDate: "2026-06-01");
        await AddLineAsync(c, original, chart.Sales, credit: 800m);
        await TransitionAsync(c, "finance.voucher", original, "post");
        var entry = Assert.Single(await QueryAsync(c, "finance.journalentry", $"voucher eq '{Id(original)}'"));

        // The reversal voucher starts with the opposite lines, filled in by the app
        var reversal = await CreateVoucherAsync(c, "2026-05-10", "Motpostering", reverses: Id(entry));
        var lines = await QueryAsync(c, "finance.voucherline", $"voucher eq '{Id(reversal)}'");
        Assert.Equal(2, lines.Count);
        Assert.Equal(800m, lines.Sum(l => Decimal(l, "credit")));
        Assert.Equal(800m, lines.Sum(l => Decimal(l, "debit")));
        Assert.Equal(Id(customer), lines.Single(l => Decimal(l, "credit") == 800m && l.GetProperty("fields").TryGetProperty("party", out var p) && p.ValueKind == JsonValueKind.String).GetProperty("fields").GetProperty("party").GetGuid());

        await TransitionAsync(c, "finance.voucher", reversal, "post");
        var reversalEntry = (await QueryAsync(c, "finance.journalentry", $"voucher eq '{Id(reversal)}'")).Single();
        Assert.Equal(Id(entry).ToString(), Field(reversalEntry, "reversalOf"));

        // Each account nets to zero
        var net = await AggregateAsync(c, "finance.posting", new
        {
            groupBy = new[] { "account" },
            filter = $"entry in ('{Id(entry)}', '{Id(reversalEntry)}')",
            measures = new object[] { new { function = "sum", field = "debit", @as = "debit" }, new { function = "sum", field = "credit", @as = "credit" } }
        });
        Assert.All(net, r => Assert.Equal(r.GetProperty("debit").GetDecimal(), r.GetProperty("credit").GetDecimal()));

        // A second reversal of the same entry is refused
        var again = await CreateVoucherAsync(c, "2026-05-11", "Motpostering igjen", reverses: Id(entry));
        using (var second = await c.PostAsync($"{Url("finance.voucher")}/{Id(again)}/transitions/post", null))
        {
            Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        }

        // The sub-ledger now has +800 and -800 for the customer; settling them closes both
        var items = await QueryAsync(c, "finance.openitem", $"party eq '{Id(customer)}'");
        Assert.Equal(2, items.Count);
        var plus = items.Single(i => Decimal(i, "amount") == 800m);
        var minus = items.Single(i => Decimal(i, "amount") == -800m);

        var settlement = await CreateAsync(c, "finance.settlement", new { settlementDate = "2026-05-12", description = "Utligning" });
        await CreateAsync(c, "finance.settlementitem", new { settlement = Id(settlement), openItem = Id(plus), amount = 800m });

        // One line only, and a sum that is not zero, are refused
        using (var tooFew = await c.PostAsync($"{Url("finance.settlement")}/{Id(settlement)}/transitions/complete", null))
        {
            Assert.Equal(HttpStatusCode.BadRequest, tooFew.StatusCode);
        }

        var wrong = await CreateAsync(c, "finance.settlementitem", new { settlement = Id(settlement), openItem = Id(minus), amount = -500m });
        using (var notZero = await c.PostAsync($"{Url("finance.settlement")}/{Id(settlement)}/transitions/complete", null))
        {
            Assert.Equal(HttpStatusCode.BadRequest, notZero.StatusCode);
            Assert.Contains("går ikke opp", await notZero.Content.ReadAsStringAsync());
        }

        using (var fix = await PatchAsync(c, "finance.settlementitem", wrong, new { amount = -800m }))
        {
            Assert.Equal(HttpStatusCode.OK, fix.StatusCode);
        }

        var completed = await TransitionAsync(c, "finance.settlement", settlement, "complete");
        Assert.Equal("completed", Field(completed, "status"));
        foreach (var item in await QueryAsync(c, "finance.openitem", $"party eq '{Id(customer)}'"))
        {
            Assert.Equal("settled", Field(item, "status"));
            Assert.Equal(0m, Decimal(item, "remaining"));
        }

        // A settled item cannot be settled again
        var reuse = await CreateAsync(c, "finance.settlement", new { settlementDate = "2026-05-13" });
        await CreateAsync(c, "finance.settlementitem", new { settlement = Id(reuse), openItem = Id(plus), amount = 800m });
        await CreateAsync(c, "finance.settlementitem", new { settlement = Id(reuse), openItem = Id(minus), amount = -800m });
        using (var reused = await c.PostAsync($"{Url("finance.settlement")}/{Id(reuse)}/transitions/complete", null))
        {
            Assert.Equal(HttpStatusCode.BadRequest, reused.StatusCode);
        }
    }

    [Fact]
    public async Task ANewFiscalYear_GetsOnePeriodPerMonth()
    {
        await using var env = await SetUpAsync(withYear: false);
        var c = env.Client;

        var year = await CreateAsync(c, "core.fiscalyear", new { year = 2026, startDate = "2026-01-01", endDate = "2026-12-31" });
        var periods = await WaitForAsync(async () =>
        {
            var found = await QueryAsync(c, "core.fiscalperiod", $"fiscalYear eq '{Id(year)}'", orderBy: "number", top: 50);
            return found.Count == 12 ? found : null;
        });

        Assert.Equal(12, periods.Count);
        Assert.Equal("2026-01-01", Field(periods[0], "startDate"));
        Assert.Equal("2026-01-31", Field(periods[0], "endDate"));
        Assert.Equal("2026-02-28", Field(periods[1], "endDate"));
        Assert.Equal("2026-12-31", Field(periods[11], "endDate"));
    }

    // ------------------------------------------------------------------ set-up

    private async Task<Env> SetUpAsync(bool withYear = true)
    {
        var factory = CreateFactory();
        var host = await StartContainerAsync<FinanceApp>(factory);
        factory.Services.GetRequiredService<KubernetesAppOrchestrator>().RegisterEndpoint(
            "finance", "1.0.0", "http://finance:50051", () => host.GetTestServer().CreateHandler());

        var client = factory.CreateClient();
        using (var res = await client.PostAsJsonAsync("/api/v1/auth/dev-login", new { }))
        {
            res.EnsureSuccessStatusCode();
            var token = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("accessToken").GetString();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var env = new Env(factory, host, client, fixture.MigrationConnectionString);
        foreach (var app in new[] { "core.parties", "core.dimensions", "core.organization", "finance" })
        {
            using var res = await client.PostAsync($"/api/v1/apps/install-builtin/{app}", null);
            Assert.True(res.IsSuccessStatusCode, $"{app}: {await res.Content.ReadAsStringAsync()}");
        }

        if (withYear)
        {
            var year = await CreateAsync(client, "core.fiscalyear", new { year = 2026, startDate = "2026-01-01", endDate = "2026-12-31" });
            await WaitForAsync(async () =>
            {
                var found = await QueryAsync(client, "core.fiscalperiod", $"fiscalYear eq '{Id(year)}'", top: 50);
                return found.Count == 12 ? found : null;
            });
        }

        return env;
    }

    private static async Task<Chart> CreateChartAsync(HttpClient c) => new(
        await CreateAsync(c, "finance.account", new { number = "1920", name = "Bank", accountType = "asset" }),
        await CreateAsync(c, "finance.account", new { number = "1500", name = "Kundefordringer", accountType = "asset", controlType = "receivable" }),
        await CreateAsync(c, "finance.account", new { number = "2400", name = "Leverandørgjeld", accountType = "liability", controlType = "payable" }),
        await CreateAsync(c, "finance.account", new { number = "3000", name = "Salgsinntekt", accountType = "revenue" }),
        await CreateAsync(c, "finance.account", new { number = "6300", name = "Leie lokaler", accountType = "expense" }),
        await CreateAsync(c, "finance.account", new { number = "9999", name = "Utgått", accountType = "expense", isActive = false }));

    private static Task<JsonElement> CreatePartyAsync(HttpClient c, string number) => CreateAsync(c, "core.party", new
    {
        partyNumber = number,
        name = $"Kunde {number}",
        partyType = "organization",
        role = "customer",
        organizationNumber = "955777888"
    });

    private static Task<JsonElement> CreateVoucherAsync(HttpClient c, string date, string text, Guid? reverses = null) =>
        CreateAsync(c, "finance.voucher", reverses is null
            ? new Dictionary<string, object?> { ["voucherDate"] = date, ["description"] = text }
            : new Dictionary<string, object?> { ["voucherDate"] = date, ["description"] = text, ["reverses"] = reverses });

    private static Task<JsonElement> AddLineAsync(
        HttpClient c, JsonElement voucher, JsonElement account, decimal debit = 0, decimal credit = 0, Guid? party = null, string? dueDate = null) =>
        CreateAsync(c, "finance.voucherline", new Dictionary<string, object?>
        {
            ["voucher"] = Id(voucher),
            ["account"] = Id(account),
            ["debit"] = debit,
            ["credit"] = credit,
            ["party"] = party,
            ["dueDate"] = dueDate
        });

    private static async Task<JsonElement> PostSimpleAsync(HttpClient c, Chart chart, string date)
    {
        var voucher = await CreateVoucherAsync(c, date, "Enkelt bilag");
        await AddLineAsync(c, voucher, chart.Bank, debit: 10m);
        await AddLineAsync(c, voucher, chart.Sales, credit: 10m);
        await TransitionAsync(c, "finance.voucher", voucher, "post");
        return (await QueryAsync(c, "finance.journalentry", $"voucher eq '{Id(voucher)}'")).Single();
    }

    private async Task AssertDatabaseRefusesAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(fixture.ApplicationConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand($"SELECT set_config('app.tenant_id', '{DemoTenantSeeder.DemoTenantId}', true);", conn, tx))
        {
            await set.ExecuteNonQueryAsync();
        }

        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        var error = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("23000", error.SqlState);
    }

    // ------------------------------------------------------------------ http helpers

    private static string Url(string objectId) => $"/api/v1/{DemoTenantSeeder.DemoCompanyId}/{objectId}";

    private static Guid Id(JsonElement record) => record.GetProperty("id").GetGuid();

    private static string? Field(JsonElement record, string name) =>
        record.GetProperty("fields").TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : null;

    private static decimal Decimal(JsonElement record, string name) => record.GetProperty("fields").GetProperty(name).GetDecimal();

    private static long Sequence(JsonElement entry) => long.Parse(Field(entry, "entryNumber")!.Split('-')[^1]);

    private static async Task<JsonElement> CreateAsync(HttpClient c, string objectId, object body)
    {
        using var res = await c.PostAsJsonAsync(Url(objectId), body);
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"create {objectId}: {(int)res.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<List<JsonElement>> QueryAsync(HttpClient c, string objectId, string filter, string? orderBy = null, int top = 200)
    {
        var url = $"{Url(objectId)}?filter={Uri.EscapeDataString(filter)}&top={top}" + (orderBy is null ? string.Empty : $"&orderBy={Uri.EscapeDataString(orderBy)}");
        var res = await c.GetFromJsonAsync<JsonElement>(url);
        return res.EnumerateArray().Select(e => e.Clone()).ToList();
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
