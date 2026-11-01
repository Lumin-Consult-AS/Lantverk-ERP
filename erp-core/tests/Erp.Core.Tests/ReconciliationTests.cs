using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Bank.Iso20022;
using Erp.Core.Domain.Tenancy;
using Erp.Core.Infrastructure.Apps;
using Erp.Core.Infrastructure.Documents;
using Erp.Core.Infrastructure.Persistence;
using Erp.Core.Infrastructure.Storage;
using Erp.Sdk;
using Finance;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Norway;
using Npgsql;
using Xunit;

namespace Erp.Core.Tests;

/// <summary>KID and bank reconciliation: a bank file in, receipts and payments matched and settled, the rest left for review.</summary>
[Collection(PostgresDatabaseCollection.Name)]
public sealed class ReconciliationTests(PostgresDatabaseFixture fixture)
{
    private const string TestOidcSigningKey = "test-only-external-oidc-signing-key-at-least-32-bytes!";
    private const string TestOidcIssuer = "https://oidc.test.local/realms/erp-dev";
    private const string TestOidcAudience = "erp-web";
    private const string BankNumber = "12345678903";

    private sealed record Env(
        WebApplicationFactory<Program> Factory, WebApplication Finance, WebApplication Norway, WebApplication Bank, HttpClient Client, string MigrationConnectionString)
        : IAsyncDisposable
    {
        private static readonly string[] FinanceTables =
            ["bankreconciliation", "banktransaction", "bankstatement", "payment", "paymentbatch", "bankaccount", "settlementitem", "settlement",
             "openitem", "posting", "journalentry", "voucherline", "voucher", "account"];

        public async ValueTask DisposeAsync()
        {
            foreach (var app in new[] { "bank.iso20022", "norway" })
            {
                using var uninstall = await Client.DeleteAsync($"/api/v1/apps/{app}?purge=true");
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
                    + "\nDELETE FROM core.number_counter WHERE object_id LIKE 'finance.%' OR object_id LIKE 'norway.%';",
                    conn);
                await cmd.ExecuteNonQueryAsync();
            }

            var archives = await Client.GetFromJsonAsync<JsonElement>("/api/v1/apps/archives");
            foreach (var archive in archives.EnumerateArray().Where(a => a.GetProperty("appId").GetString() is "finance" or "bank.iso20022" or "norway"))
            {
                await Client.PostAsync($"/api/v1/apps/archives/{archive.GetProperty("id").GetGuid()}/purge", null);
            }

            foreach (var app in new[] { "core.currencies", "core.organization", "core.dimensions", "core.parties" })
            {
                await Client.DeleteAsync($"/api/v1/apps/{app}?purge=true");
            }

            Client.Dispose();
            await Bank.DisposeAsync();
            await Norway.DisposeAsync();
            await Finance.DisposeAsync();
            await Factory.DisposeAsync();
        }
    }

    // ------------------------------------------------------------------ KID

    [Fact]
    public void Kid_IsMadeWithMod10_AndChecked_ByMod10OrMod11()
    {
        Assert.Equal("1234567897", Kid.WithMod10("123456789"));
        Assert.Equal("0000000018", Kid.WithMod10("000000001"));
        Assert.Equal("1234567892", Kid.WithMod11("123456789"));
        Assert.Null(Kid.WithMod11("6")); // no valid MOD11 digit for this number

        Assert.True(Kid.IsValid("1234567897"));
        Assert.True(Kid.IsValid("1234567892"));
        Assert.False(Kid.IsValid("1234567890"));
        Assert.False(Kid.IsValid("1234567898"));
        Assert.False(Kid.IsValid("12A4567897"));
        Assert.False(Kid.IsValid("7")); // too short
        Assert.False(Kid.IsValid(new string('1', 26))); // too long
        Assert.False(Kid.IsValid(null));
    }

    // ------------------------------------------------------------------ reconciliation

    [Fact]
    public async Task APaymentWithKid_SettlesTheRightCustomerItem_WithoutManualWork_AndTheRestWaitsForReview()
    {
        await using var env = await SetUpAsync();
        var c = env.Client;
        var a = await AccountsAsync(c);
        var bank = await CreateAsync(c, "finance.bankaccount", new
        {
            name = "Driftskonto", accountNumber = BankNumber, holderName = "Demohandel AS", ledgerAccount = Id(a["1920"]), currency = "NOK"
        });
        var customer = await CreatePartyAsync(c, "K-1", "Kunde 1 AS", "customer");
        var supplier = await CreatePartyAsync(c, "L-1", "Leverandør 1 AS", "supplier");

        // Two customer invoices and one supplier invoice. Each customer invoice gets its own KID.
        await PostAsync(c, "2026-03-10", "Faktura 1001", (a["1500"], 1250m, 0m, customer, "2026-04-10"), (a["3000"], 0m, 1250m, null, null));
        await PostAsync(c, "2026-03-11", "Faktura 1002", (a["1500"], 1000m, 0m, customer, "2026-04-11"), (a["3000"], 0m, 1000m, null, null));
        await PostAsync(c, "2026-03-12", "Faktura L-5", (a["6300"], 800m, 0m, null, null), (a["2400"], 0m, 800m, supplier, "2026-04-12"));
        var items = await QueryAsync(c, "finance.openitem", "status eq 'open'");
        var invoice1 = items.Single(i => Decimal(i, "amount") == 1250m);
        var invoice2 = items.Single(i => Decimal(i, "amount") == 1000m);
        var payable = items.Single(i => Decimal(i, "amount") == -800m);
        var kid1 = Field(invoice1, "paymentReference")!;
        var kid2 = Field(invoice2, "paymentReference")!;
        Assert.True(Kid.IsValid(kid1) && Kid.IsValid(kid2));
        Assert.NotEqual(kid1, kid2);
        Assert.Matches(@"^\d{10}$", kid1);
        Assert.Null(Field(payable, "paymentReference")); // suppliers' items get none

        // A payment to the supplier: its id is what the bank sends back as the end-to-end id
        var batch = await CreateAsync(c, "finance.paymentbatch", new
        {
            name = "Uke 15", bankAccount = Id(bank), executionDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(3).ToString("yyyy-MM-dd")
        });
        var payment = await CreateAsync(c, "finance.payment", new
        {
            batch = Id(batch), openItem = Id(payable), party = Id(supplier), creditorName = "Leverandør 1 AS", creditorAccount = "98765432109", amount = 800m, currency = "NOK"
        });

        // The bank statement: a full payment with KID, a part payment with KID, a payment nobody can place, and the supplier payment
        var statement = Statement(
            ("REF1", "CRDT", 1250m, "Kunde 1 AS", kid1, null),
            ("REF2", "CRDT", 600m, "Kunde 1 AS", kid2, null),
            ("REF3", "CRDT", 77m, "Ukjent innbetaler", null, null),
            ("REF4", "DBIT", 800m, "Leverandør 1 AS", null, Id(payment).ToString("N")));
        var imported = await ImportAsync(c, await UploadAsync(c, "utskrift.xml", statement), Id(bank));
        Assert.True(Field(imported, "status") == "done", Field(imported, "message"));
        Assert.Equal(4, int.Parse(Field(imported, "transactionCount")!));

        var transactions = await WaitForAsync(async () =>
        {
            var found = await QueryAsync(c, "finance.banktransaction", "externalId ne ''");
            // Done when none is new, and none waits in review with the note of a match that is about to be accepted
            return found.Count == 4 && found.All(t => Field(t, "status") != "new" && !(Field(t, "status") == "review" && Field(t, "note")!.Contains("stemmer"))) ? found : null;
        });
        JsonElement Tx(string reference) => transactions.Single(t => Field(t, "externalId")!.EndsWith(":" + reference));

        // The full payment with the right KID settled invoice 1 by itself: posted against the bank, the item settled
        var full = Tx("REF1");
        Assert.True(Field(full, "status") == "matched", Field(full, "note"));
        Assert.Equal(Id(invoice1), Guid.Parse(Field(full, "openItem")!));
        Assert.NotNull(Field(full, "voucher"));
        Assert.NotNull(Field(full, "settlement"));
        var settled1 = await GetAsync(c, "finance.openitem", Id(invoice1));
        Assert.Equal("settled", Field(settled1, "status"));
        Assert.Equal(0m, Decimal(settled1, "remaining"));
        var entry = (await QueryAsync(c, "finance.journalentry", $"voucher eq '{Field(full, "voucher")}'")).Single();
        var postings = await QueryAsync(c, "finance.posting", $"entry eq '{Id(entry)}'");
        Assert.Equal(1250m, Decimal(postings.Single(p => Guid.Parse(Field(p, "account")!) == Id(a["1920"])), "debit"));
        Assert.Equal(1250m, Decimal(postings.Single(p => Guid.Parse(Field(p, "account")!) == Id(a["1500"])), "credit"));

        // The payment to the supplier was found by its end-to-end id
        var paid = Tx("REF4");
        Assert.True(Field(paid, "status") == "matched", Field(paid, "note"));
        Assert.Equal("settled", Field(await GetAsync(c, "finance.openitem", Id(payable)), "status"));
        var paidEntry = (await QueryAsync(c, "finance.journalentry", $"voucher eq '{Field(paid, "voucher")}'")).Single();
        var paidPostings = await QueryAsync(c, "finance.posting", $"entry eq '{Id(paidEntry)}'");
        Assert.Equal(800m, Decimal(paidPostings.Single(p => Guid.Parse(Field(p, "account")!) == Id(a["2400"])), "debit"));
        Assert.Equal(800m, Decimal(paidPostings.Single(p => Guid.Parse(Field(p, "account")!) == Id(a["1920"])), "credit"));

        // The part payment and the unknown payment wait: the first with a suggestion, the second with none
        var part = Tx("REF2");
        Assert.Equal("review", Field(part, "status"));
        Assert.Equal(Id(invoice2), Guid.Parse(Field(part, "suggestedOpenItem")!));
        Assert.Contains("peker på", Field(part, "note"));
        var unknown = Tx("REF3");
        Assert.Equal("review", Field(unknown, "status"));
        Assert.Null(Field(unknown, "suggestedOpenItem"));
        Assert.Contains("Ingen treff", Field(unknown, "note"));

        // They show up on the start page as work to do
        var attention = await c.GetFromJsonAsync<JsonElement>("/api/v1/attention");
        var review = attention.EnumerateArray().Single(i => i.GetProperty("id").GetString() == "finance.banktransactions.review");
        Assert.Equal(2, review.GetProperty("count").GetInt32());

        // The reconciliation against the ledger: the bank says 11 127.00, the ledger 450.00 (opening balance is not in the books)
        var recon = await CreateAsync(c, "finance.bankreconciliation", new { bankAccount = Id(bank), asOf = "2026-04-30" });
        recon = await GetAsync(c, "finance.bankreconciliation", Id(recon));
        Assert.Equal(11127m, Decimal(recon, "bankBalance"));
        Assert.Equal(450m, Decimal(recon, "ledgerBalance"));
        Assert.Equal(10677m, Decimal(recon, "difference"));
        Assert.Equal(2, int.Parse(Field(recon, "unmatchedCount")!));
        Assert.Equal(677m, Decimal(recon, "unmatchedAmount"));

        // A person accepts the suggestion: the part payment settles part of invoice 2
        var accepted = await TransitionAsync(c, "finance.banktransaction", part, "accept");
        Assert.Equal("matched", Field(accepted, "status"));
        var partlySettled = await GetAsync(c, "finance.openitem", Id(invoice2));
        Assert.Equal("open", Field(partlySettled, "status"));
        Assert.Equal(400m, Decimal(partlySettled, "remaining"));

        // Accepting something without a suggestion is refused; it can be ignored
        using (var refused = await c.PostAsync($"{Url("finance.banktransaction")}/{Id(unknown)}/transitions/accept", null))
        {
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("åpen post", await refused.Content.ReadAsStringAsync());
        }

        Assert.Equal("ignored", Field(await TransitionAsync(c, "finance.banktransaction", unknown, "ignore"), "status"));

        // Reading the same file again changes nothing
        var again = await ImportAsync(c, await UploadAsync(c, "utskrift.xml", statement), Id(bank));
        Assert.Equal(0, int.Parse(Field(again, "transactionCount")!));
        Assert.Equal(4, (await QueryAsync(c, "finance.banktransaction", "externalId ne ''")).Count);
    }

    [Fact]
    public async Task APaymentReference_MustBeAValidKid()
    {
        await using var env = await SetUpAsync();
        var c = env.Client;
        var a = await AccountsAsync(c);
        var bank = await CreateAsync(c, "finance.bankaccount", new
        {
            name = "Driftskonto", accountNumber = BankNumber, holderName = "Demohandel AS", ledgerAccount = Id(a["1920"]), currency = "NOK"
        });
        var supplier = await CreatePartyAsync(c, "L-2", "Leverandør 2 AS", "supplier");
        var batch = await CreateAsync(c, "finance.paymentbatch", new
        {
            name = "KID", bankAccount = Id(bank), executionDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(3).ToString("yyyy-MM-dd")
        });

        using (var bad = await c.PostAsJsonAsync(Url("finance.payment"), new
               {
                   batch = Id(batch), party = Id(supplier), creditorName = "L", creditorAccount = "98765432109", amount = 10m, currency = "NOK", paymentReference = "1234567890"
               }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            Assert.Contains("gyldig KID", await bad.Content.ReadAsStringAsync());
        }

        await CreateAsync(c, "finance.payment", new
        {
            batch = Id(batch), party = Id(supplier), creditorName = "L", creditorAccount = "98765432109", amount = 10m, currency = "NOK", paymentReference = "1234567897"
        });
    }

    // ------------------------------------------------------------------ bank file

    private static byte[] Statement(params (string Reference, string Direction, decimal Amount, string Name, string? Kid, string? EndToEnd)[] entries)
    {
        var entryXml = new StringBuilder();
        foreach (var (reference, direction, amount, name, kid, endToEnd) in entries)
        {
            var credit = direction == "CRDT";
            entryXml.Append($"""
                <Ntry>
                  <Amt Ccy="NOK">{amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}</Amt><CdtDbtInd>{direction}</CdtDbtInd><Sts>BOOK</Sts>
                  <BookgDt><Dt>2026-04-14</Dt></BookgDt><ValDt><Dt>2026-04-14</Dt></ValDt>
                  <AcctSvcrRef>{reference}</AcctSvcrRef>
                  <BkTxCd/>
                  <NtryDtls><TxDtls>
                    {(endToEnd is null ? "" : $"<Refs><EndToEndId>{endToEnd}</EndToEndId></Refs>")}
                    <RltdPties>{(credit ? $"<Dbtr><Nm>{name}</Nm></Dbtr>" : $"<Cdtr><Nm>{name}</Nm></Cdtr>")}</RltdPties>
                    {(kid is null ? "" : $"<RmtInf><Strd><CdtrRefInf><Tp><CdOrPrtry><Prtry>KID</Prtry></CdOrPrtry></Tp><Ref>{kid}</Ref></CdtrRefInf></Strd></RmtInf>")}
                  </TxDtls></NtryDtls>
                </Ntry>
                """);
        }

        var closing = 10000m + entries.Sum(e => e.Direction == "CRDT" ? e.Amount : -e.Amount);
        return Encoding.UTF8.GetBytes($"""
            <?xml version="1.0" encoding="UTF-8"?>
            <Document xmlns="urn:iso:std:iso:20022:tech:xsd:camt.053.001.02">
              <BkToCstmrStmt>
                <GrpHdr><MsgId>MSG-1</MsgId><CreDtTm>2026-04-30T23:00:00</CreDtTm></GrpHdr>
                <Stmt>
                  <Id>STMT-2026-04</Id>
                  <CreDtTm>2026-04-30T23:00:00</CreDtTm>
                  <FrToDt><FrDtTm>2026-04-01T00:00:00</FrDtTm><ToDtTm>2026-04-30T23:59:59</ToDtTm></FrToDt>
                  <Acct><Id><Othr><Id>{BankNumber}</Id></Othr></Id><Ccy>NOK</Ccy></Acct>
                  <Bal><Tp><CdOrPrtry><Cd>OPBD</Cd></CdOrPrtry></Tp><Amt Ccy="NOK">10000.00</Amt><CdtDbtInd>CRDT</CdtDbtInd><Dt><Dt>2026-04-01</Dt></Dt></Bal>
                  <Bal><Tp><CdOrPrtry><Cd>CLBD</Cd></CdOrPrtry></Tp><Amt Ccy="NOK">{closing.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}</Amt><CdtDbtInd>CRDT</CdtDbtInd><Dt><Dt>2026-04-30</Dt></Dt></Bal>
                  {entryXml}
                </Stmt>
              </BkToCstmrStmt>
            </Document>
            """);
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<Dictionary<string, JsonElement>> AccountsAsync(HttpClient c) =>
        (await QueryAsync(c, "finance.account", "number ge '0'", 500)).ToDictionary(x => Field(x, "number")!);

    private static Task<JsonElement> CreatePartyAsync(HttpClient c, string number, string name, string role) =>
        CreateAsync(c, "core.party", new { partyNumber = number, name, partyType = "organization", role, organizationNumber = "955777888" });

    private static async Task PostAsync(
        HttpClient c, string date, string text, params (JsonElement Account, decimal Debit, decimal Credit, JsonElement? Party, string? Due)[] lines)
    {
        var voucher = await CreateAsync(c, "finance.voucher", new { voucherDate = date, description = text });
        foreach (var (account, debit, credit, party, due) in lines)
        {
            await CreateAsync(c, "finance.voucherline", new Dictionary<string, object?>
            {
                ["voucher"] = Id(voucher), ["account"] = Id(account), ["debit"] = debit, ["credit"] = credit,
                ["party"] = party is { } p ? Id(p) : null, ["dueDate"] = due
            });
        }

        await TransitionAsync(c, "finance.voucher", voucher, "post");
        // The KID and the VAT entries are made by the Norway package after the posting
        await Task.Delay(100);
    }

    private static async Task<Guid> UploadAsync(HttpClient c, string name, byte[] content)
    {
        var form = new MultipartFormDataContent { { new ByteArrayContent(content), "file", name } };
        using var res = await c.PostAsync("/api/v1/files", form);
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, text);
        return JsonDocument.Parse(text).RootElement.GetProperty("file").GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> ImportAsync(HttpClient c, Guid file, Guid bankAccount)
    {
        var requested = await CreateAsync(c, "bank.import", new { file, bankAccount });
        var done = await WaitForAsync(async () =>
        {
            var current = await GetAsync(c, "bank.import", Id(requested));
            return Field(current, "status") != "requested" ? [current] : null;
        });
        return done[0];
    }

    private async Task<Env> SetUpAsync()
    {
        var factory = CreateFactory();
        var finance = await StartContainerAsync<FinanceApp>(factory);
        var norway = await StartContainerAsync<NorwayApp>(factory);
        var bank = await StartContainerAsync<BankApp>(factory);
        var orchestrator = factory.Services.GetRequiredService<KubernetesAppOrchestrator>();
        orchestrator.RegisterEndpoint("finance", "1.0.0", "http://finance:50051", () => finance.GetTestServer().CreateHandler());
        orchestrator.RegisterEndpoint("norway", "1.0.0", "http://norway:50051", () => norway.GetTestServer().CreateHandler());
        orchestrator.RegisterEndpoint("bank.iso20022", "1.0.0", "http://bank:50051", () => bank.GetTestServer().CreateHandler());

        var client = factory.CreateClient();
        using (var res = await client.PostAsJsonAsync("/api/v1/auth/dev-login", new { }))
        {
            res.EnsureSuccessStatusCode();
            var token = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("accessToken").GetString();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var env = new Env(factory, finance, norway, bank, client, fixture.MigrationConnectionString);
        foreach (var app in new[] { "core.parties", "core.dimensions", "core.organization", "core.currencies", "finance", "norway", "bank.iso20022" })
        {
            using var res = await client.PostAsync($"/api/v1/apps/install-builtin/{app}", null);
            Assert.True(res.IsSuccessStatusCode, $"{app}: {await res.Content.ReadAsStringAsync()}");
        }

        var year = await CreateAsync(client, "core.fiscalyear", new { year = 2026, startDate = "2026-01-01", endDate = "2026-12-31" });
        await WaitForAsync(async () =>
        {
            var periods = await QueryAsync(client, "core.fiscalperiod", $"fiscalYear eq '{Id(year)}'", 50);
            var settings = await QueryAsync(client, "norway.settings", "singleton eq 'settings'");
            return periods.Count == 12 && settings.Count == 1 && Field(settings[0], "chartVersion") == NorwayApp.ChartVersion ? periods : null;
        });
        return env;
    }

    private static string Url(string objectId) => $"/api/v1/{DemoTenantSeeder.DemoCompanyId}/{objectId}";

    private static Guid Id(JsonElement record) => record.GetProperty("id").GetGuid();

    private static string? Field(JsonElement record, string name) =>
        record.GetProperty("fields").TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : null;

    private static decimal Decimal(JsonElement record, string name) => record.GetProperty("fields").GetProperty(name).GetDecimal();

    private static async Task<JsonElement> GetAsync(HttpClient c, string objectId, Guid id) =>
        (await c.GetFromJsonAsync<JsonElement>($"{Url(objectId)}/{id}")).Clone();

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

    private static async Task<JsonElement> TransitionAsync(HttpClient c, string objectId, JsonElement record, string transition)
    {
        using var res = await c.PostAsync($"{Url(objectId)}/{Id(record)}/transitions/{transition}", null);
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{objectId} {transition}: {(int)res.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<List<JsonElement>> WaitForAsync(Func<Task<List<JsonElement>?>> probe)
    {
        for (var attempt = 0; attempt < 150; attempt++)
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

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IObjectStorage>();
                services.AddSingleton<IObjectStorage>(new InMemoryObjectStorage());
                services.RemoveAll<IVirusScanner>();
                services.AddSingleton<IVirusScanner, CleanScanner>();
            });
        });
    }

    private sealed class CleanScanner : IVirusScanner
    {
        public Task<ScanResult> ScanAsync(byte[] content, CancellationToken cancellationToken) => Task.FromResult(new ScanResult(ScanOutcome.Clean));
    }
}
