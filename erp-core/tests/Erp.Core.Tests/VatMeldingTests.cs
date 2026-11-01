using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
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
using Norway.Mva;
using Npgsql;
using Xunit;

namespace Erp.Core.Tests;

/// <summary>The VAT return in Skatteetaten's format (mva-melding v1.0): made from the term's VAT, checked, and the deadlines kept in sight.</summary>
[Collection(PostgresDatabaseCollection.Name)]
public sealed class VatMeldingTests(PostgresDatabaseFixture fixture)
{
    private const string TestOidcSigningKey = "test-only-external-oidc-signing-key-at-least-32-bytes!";
    private const string TestOidcIssuer = "https://oidc.test.local/realms/erp-dev";
    private const string TestOidcAudience = "erp-web";

    private static string Example(string name) => File.ReadAllText(Path.Combine(System.AppContext.BaseDirectory, "TestData", "mva", name), Encoding.UTF8);

    // ------------------------------------------------------------------ the format

    [Fact]
    public void Skatteetatens_OwnExamples_PassOurCopyOfTheSchema()
    {
        foreach (var example in new[] { "ex_mvakode3.xml", "ex_mvakode1.xml", "ex_alle.xml" })
        {
            Assert.Empty(VatMelding.SchemaErrors(XDocument.Parse(Example(example))));
        }

        Assert.NotEmpty(VatMelding.SchemaErrors(XDocument.Parse("<mvaMeldingDto xmlns=\"" + VatMelding.Ns + "\"/>")));
    }

    [Fact]
    public void AMessageForTheOutputExample_IsTheSameDocumentAsSkatteetatensOwn()
    {
        var melding = new Melding("3", "zzx", "4.34.3", "123456789", "alminnelig", "skattleggingsperiodeToMaaneder", "mars-april", 2021,
            "34000009324568708", [new MeldingLine("3", "25 utgående mva", 20000, "25", 5000)]);
        var document = VatMelding.Write(melding);
        var official = XDocument.Parse(Example("ex_mvakode3.xml")).Root!;
        official.Descendants(VatMelding.Ns + "mvaKodeRegnskapsystem").Single().Value = "25 utgående mva"; // their copy of this text is damaged
        // Compared as text: the namespace declaration is an attribute in a parsed document and implied in a built one
        Assert.True(XNode.DeepEquals(official, XElement.Parse(document.Root!.ToString())), document.Root.ToString());

        // Input VAT is negative and has neither basis nor rate, as in their example for code 1
        var input = new Melding("1", "zzx", "4.34.3", "913238254", "alminnelig", "skattleggingsperiodeToMaaneder", "mars-april", 2021,
            "34000009324568708", [new MeldingLine("1", "25 inng mva", null, null, -2500)]);
        var written = VatMelding.Write(input).Root!;
        var theirs = XDocument.Parse(Example("ex_mvakode1.xml")).Root!;
        theirs.Element(VatMelding.Ns + "merknad")!.Remove(); // their example carries a free-text note
        Assert.True(XNode.DeepEquals(theirs, XElement.Parse(written.ToString())), written.ToString());
        Assert.Equal(-2500, input.Total);
    }

    [Fact]
    public void ATermIsGivenItsPeriodCode_OrRefused_AndTheDeadlineIsTheTenthOfTheSecondMonthAfter()
    {
        Assert.Equal(("skattleggingsperiodeToMaaneder", "januar-februar", 2026), VatMelding.Period(new DateOnly(2026, 1, 1), new DateOnly(2026, 2, 28)));
        Assert.Equal(("skattleggingsperiodeToMaaneder", "november-desember", 2026), VatMelding.Period(new DateOnly(2026, 11, 1), new DateOnly(2026, 12, 31)));
        Assert.Equal(("skattleggingsperiodeMaaned", "mars", 2026), VatMelding.Period(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31)));
        Assert.Equal(("skattleggingsperiodeTreMaaneder", "april-juni", 2026), VatMelding.Period(new DateOnly(2026, 4, 1), new DateOnly(2026, 6, 30)));
        Assert.Equal(("skattleggingsperiodeAar", "aarlig", 2026), VatMelding.Period(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)));
        Assert.Null(VatMelding.Period(new DateOnly(2026, 2, 1), new DateOnly(2026, 3, 31))); // February-March is not a term
        Assert.Null(VatMelding.Period(new DateOnly(2026, 1, 10), new DateOnly(2026, 2, 28)));

        Assert.Equal(new DateOnly(2026, 4, 10), VatMelding.DueDate(new DateOnly(2026, 2, 28)));
        Assert.Equal(new DateOnly(2027, 2, 10), VatMelding.DueDate(new DateOnly(2026, 12, 31)));
        Assert.Equal(new DateOnly(2027, 4, 12), VatMelding.DueDate(new DateOnly(2027, 2, 28))); // the 10th is a Saturday
        Assert.Equal(new DateOnly(2025, 5, 12), VatMelding.DueDate(new DateOnly(2025, 3, 31))); // a Saturday again

        Assert.Equal(new DateOnly(2026, 4, 5), NorwegianHolidays.EasterSunday(2026));
        Assert.Equal(new DateOnly(2025, 4, 20), NorwegianHolidays.EasterSunday(2025));
        Assert.All(new[] { new DateOnly(2026, 4, 2), new DateOnly(2026, 4, 3), new DateOnly(2026, 4, 6), new DateOnly(2026, 5, 14), new DateOnly(2026, 5, 25), new DateOnly(2026, 5, 17) },
            d => Assert.True(NorwegianHolidays.IsHoliday(d), d.ToString()));
        Assert.False(NorwegianHolidays.IsHoliday(new DateOnly(2026, 4, 10)));
        Assert.Equal("11,11", VatMelding.RateText(11.11m));
        Assert.Equal("25", VatMelding.RateText(25m));
    }

    [Fact]
    public void TheBusinessRules_StopWhatSkatteetatenWouldRefuse()
    {
        Melding With(IReadOnlyList<MeldingLine> lines, string org = "999888777", string? kid = null) =>
            new("ref", "Laftverk ERP", "1.0", org, "alminnelig", "skattleggingsperiodeToMaaneder", "januar-februar", 2026, kid, lines);

        var good = With([new MeldingLine("3", "3", 1000, "25", 250), new MeldingLine("1", "1", null, null, -200)]);
        Assert.Empty(VatMelding.Validate(good, VatMelding.Write(good)));
        Assert.Equal(50, good.Total);

        void Blocks(Melding m, string rule) => Assert.Contains(VatMelding.Validate(m, VatMelding.Write(m)), i => i.Blocks && i.Rule == rule);
        Blocks(With([new MeldingLine("1", "1", null, null, -5)], org: "12345"), "R000");
        Blocks(With([new MeldingLine("3", "3", 1000, null, 250)]), "R066");
        Blocks(With([new MeldingLine("1", "1", null, null, -5)], kid: "1234567890"), "R079");


        // An output line whose VAT does not follow basis × rate is flagged, but does not stop the message
        var off = With([new MeldingLine("3", "3", 1000, "25", 300)]);
        var warning = Assert.Single(VatMelding.Validate(off, VatMelding.Write(off)));
        Assert.False(warning.Blocks);
        Assert.Equal("R019", warning.Rule);
    }

    // ------------------------------------------------------------------ the whole way

    [Fact]
    public async Task ACalculatedTerm_BecomesAValidMessage_AndTheDeadlineShowsOnTheStartPage()
    {
        await using var env = await SetUpAsync();
        var c = env.Client;
        var a = await AccountsAsync(c);
        var codes = (await QueryAsync(c, "norway.vatcode", "code ne ''", 100)).ToDictionary(x => Field(x, "code")!);
        var settings = (await QueryAsync(c, "norway.settings", "singleton eq 'settings'")).Single();
        using (var set = await PatchAsync(c, "norway.settings", settings, new Dictionary<string, object?> { ["organizationNumber"] = "999888777", ["companyName"] = "Demohandel AS" }))
        {
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        }

        var customer = await CreateAsync(c, "core.party", new Dictionary<string, object?>
        {
            ["partyNumber"] = "K-1", ["name"] = "Kunde AS", ["partyType"] = "organization", ["role"] = "customer", ["organizationNumber"] = "955777888"
        });
        var supplier = await CreateAsync(c, "core.party", new Dictionary<string, object?>
        {
            ["partyNumber"] = "L-1", ["name"] = "Leverandør AS", ["partyType"] = "organization", ["role"] = "supplier", ["organizationNumber"] = "944555666"
        });

        // A sale of 1 000 with 25 % output VAT, and a purchase of 800 with 25 % deductible input VAT
        await PostAsync(c, "2026-01-15", "Salg",
            (a["1500"], 1250m, 0m, Id(customer), null), (a["3000"], 0m, 1000m, null, Id(codes["3"])));
        await PostAsync(c, "2026-02-10", "Kjøp",
            (a["6300"], 800m, 0m, null, Id(codes["1"])), (a["2400"], 0m, 1000m, Id(supplier), null));
        await WaitForAsync(async () => (await QueryAsync(c, "norway.vatentry", "vatAmount ne 0")).Count >= 2 ? [default(JsonElement)] : null);

        var term = await CreateAsync(c, "norway.vatreturn", new { name = "1. termin 2026", startDate = "2026-01-01", endDate = "2026-02-28" });
        await TransitionAsync(c, "norway.vatreturn", term, "calculate");
        var calculated = await GetAsync(c, "norway.vatreturn", Id(term));
        Assert.Equal(250m, Decimal(calculated, "outputVat"));
        Assert.Equal(200m, Decimal(calculated, "inputVat"));
        Assert.Equal("2026-04-10", Field(await GetAsync(c, "norway.vatreturn", Id(term)), "deadline"));

        // The message is made and checked
        var made = await CreateAsync(c, "norway.vatsubmission", new { vatReturn = Id(term) });
        var done = await WaitForStatusAsync(c, "norway.vatsubmission", Id(made), "valid");
        Assert.Equal(50, int.Parse(Field(done, "totalAmount")!));
        Assert.Contains("Å betale: 50", Field(done, "message"));
        var xml = XDocument.Parse(Encoding.UTF8.GetString(await c.GetByteArrayAsync($"/api/v1/files/{Field(done, "file")}/content")));
        Assert.Empty(VatMelding.SchemaErrors(xml));
        XNamespace ns = VatMelding.Ns;
        var root = xml.Root!;
        Assert.Equal("999888777", root.Element(ns + "skattepliktig")!.Element(ns + "organisasjonsnummer")!.Value);
        Assert.Equal("januar-februar", root.Descendants(ns + "skattleggingsperiodeToMaaneder").Single().Value);
        Assert.Equal("50", root.Descendants(ns + "fastsattMerverdiavgift").Single().Value);
        var lines = root.Descendants(ns + "mvaSpesifikasjonslinje").ToList();
        var output = lines.Single(l => l.Element(ns + "mvaKode")!.Value == "3");
        Assert.Equal("1000", output.Element(ns + "grunnlag")!.Value);
        Assert.Equal("25", output.Element(ns + "sats")!.Value);
        Assert.Equal("250", output.Element(ns + "merverdiavgift")!.Value);
        var input = lines.Single(l => l.Element(ns + "mvaKode")!.Value == "1");
        Assert.Null(input.Element(ns + "grunnlag"));
        Assert.Equal("-200", input.Element(ns + "merverdiavgift")!.Value);

        // The deadline has passed and nothing is reported: it is on the start page until the term is marked delivered
        var attention = await c.GetFromJsonAsync<JsonElement>("/api/v1/attention");
        Assert.Contains(attention.EnumerateArray(), i => i.GetProperty("id").GetString() == "norway.vatreturn.overdue" && i.GetProperty("count").GetInt32() >= 1);
        using (var delivered = await PatchAsync(c, "norway.vatreturn", await GetAsync(c, "norway.vatreturn", Id(term)), new { submitted = true }))
        {
            Assert.Equal(HttpStatusCode.OK, delivered.StatusCode);
        }

        attention = await c.GetFromJsonAsync<JsonElement>("/api/v1/attention");
        Assert.DoesNotContain(attention.EnumerateArray(), i => i.GetProperty("id").GetString() == "norway.vatreturn.overdue");

        // A term that is not a reporting period, and one that is not calculated, say so instead of producing a message
        var odd = await CreateAsync(c, "norway.vatreturn", new { name = "Skjev", startDate = "2026-01-10", endDate = "2026-02-20" });
        await TransitionAsync(c, "norway.vatreturn", odd, "calculate");
        var oddMade = await CreateAsync(c, "norway.vatsubmission", new { vatReturn = Id(odd) });
        Assert.Contains("skattleggingsperiode", Field(await WaitForStatusAsync(c, "norway.vatsubmission", Id(oddMade), "failed"), "message"));
        var open = await CreateAsync(c, "norway.vatreturn", new { name = "Ikke beregnet", startDate = "2026-03-01", endDate = "2026-04-30" });
        var openMade = await CreateAsync(c, "norway.vatsubmission", new { vatReturn = Id(open) });
        Assert.Contains("Beregn", Field(await WaitForStatusAsync(c, "norway.vatsubmission", Id(openMade), "failed"), "message"));
    }

    // ------------------------------------------------------------------ helpers

    private sealed record Env(WebApplicationFactory<Program> Factory, WebApplication Finance, WebApplication Norway, HttpClient Client, string MigrationConnectionString)
        : IAsyncDisposable
    {
        private static readonly string[] FinanceTables = ["openitem", "posting", "journalentry", "voucherline", "voucher", "account"];

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
                    + "\nDELETE FROM core.number_counter WHERE object_id LIKE 'finance.%' OR object_id LIKE 'norway.%';",
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
            var periods = await QueryAsync(client, "core.fiscalperiod", $"fiscalYear eq '{Id(year)}'", 50);
            var found = await QueryAsync(client, "norway.settings", "singleton eq 'settings'");
            return periods.Count == 12 && found.Count == 1 && Field(found[0], "chartVersion") == NorwayApp.ChartVersion ? periods : null;
        });
        return env;
    }

    private static async Task PostAsync(HttpClient c, string date, string text, params (JsonElement Account, decimal Debit, decimal Credit, Guid? Party, Guid? VatCode)[] lines)
    {
        var voucher = await CreateAsync(c, "finance.voucher", new { voucherDate = date, description = text });
        foreach (var (account, debit, credit, party, vatCode) in lines)
        {
            await CreateAsync(c, "finance.voucherline", new Dictionary<string, object?>
            {
                ["voucher"] = Id(voucher), ["account"] = Id(account), ["debit"] = debit, ["credit"] = credit, ["party"] = party,
                ["dueDate"] = party is null ? null : "2026-04-30", ["norway.vatCode"] = vatCode
            });
        }

        await TransitionAsync(c, "finance.voucher", voucher, "post");
    }

    private static async Task<JsonElement> WaitForStatusAsync(HttpClient c, string objectId, Guid id, string status) =>
        (await WaitForAsync(async () =>
        {
            var current = await GetAsync(c, objectId, id);
            return Field(current, "status") == status ? [current] : null;
        }))[0];

    private static string Url(string objectId) => $"/api/v1/{DemoTenantSeeder.DemoCompanyId}/{objectId}";

    private static Guid Id(JsonElement record) => record.GetProperty("id").GetGuid();

    private static string? Field(JsonElement record, string name) =>
        record.GetProperty("fields").TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : null;

    private static decimal Decimal(JsonElement record, string name) => record.GetProperty("fields").GetProperty(name).GetDecimal();

    private static async Task<Dictionary<string, JsonElement>> AccountsAsync(HttpClient c) =>
        (await QueryAsync(c, "finance.account", "number ge '0'", 500)).ToDictionary(x => Field(x, "number")!);

    private static async Task<JsonElement> GetAsync(HttpClient c, string objectId, Guid id) =>
        (await c.GetFromJsonAsync<JsonElement>($"{Url(objectId)}/{id}")).Clone();

    private static async Task<JsonElement> CreateAsync(HttpClient c, string objectId, object body)
    {
        using var res = await c.PostAsJsonAsync(Url(objectId), body);
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"create {objectId}: {(int)res.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<HttpResponseMessage> PatchAsync(HttpClient c, string objectId, JsonElement record, object body)
    {
        var current = await c.GetFromJsonAsync<JsonElement>($"{Url(objectId)}/{Id(record)}");
        using var req = new HttpRequestMessage(HttpMethod.Patch, $"{Url(objectId)}/{Id(record)}");
        req.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{current.GetProperty("rowVersion").GetInt64()}\""));
        req.Content = JsonContent.Create(body);
        return await c.SendAsync(req);
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
