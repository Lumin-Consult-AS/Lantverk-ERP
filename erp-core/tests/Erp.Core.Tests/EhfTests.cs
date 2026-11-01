using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using System.Xml.Schema;
using Ehf;
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

/// <summary>EHF (PEPPOL BIS Billing 3.0): outgoing invoices become valid UBL and are handed to an access point; incoming documents become proposals.</summary>
[Collection(PostgresDatabaseCollection.Name)]
public sealed class EhfTests(PostgresDatabaseFixture fixture)
{
    private const string TestOidcSigningKey = "test-only-external-oidc-signing-key-at-least-32-bytes!";
    private const string TestOidcIssuer = "https://oidc.test.local/realms/erp-dev";
    private const string TestOidcAudience = "erp-web";

    /// <summary>An access point in memory: it keeps what was sent, and hands out what the test puts in its inbox.</summary>
    private sealed class FakeAccessPoint : IAccessPoint
    {
        public List<OutgoingDocument> Sent { get; } = [];
        public List<ReceivedDocument> Inbox { get; } = [];
        public List<string> Acknowledged { get; } = [];
        public string? FailWith { get; set; }

        public Task<string> SendAsync(OutgoingDocument document, CancellationToken cancellationToken)
        {
            if (FailWith is { } reason)
            {
                throw new AccessPointException(reason);
            }

            Sent.Add(document);
            return Task.FromResult($"AP-{Sent.Count}");
        }

        public Task<IReadOnlyList<ReceivedDocument>> ReceiveAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReceivedDocument>>(Inbox.Where(d => !Acknowledged.Contains(d.Id)).ToList());

        public Task AcknowledgeAsync(string id, CancellationToken cancellationToken)
        {
            Acknowledged.Add(id);
            return Task.CompletedTask;
        }
    }

    private sealed record Env(
        WebApplicationFactory<Program> Factory, WebApplication Finance, WebApplication Norway, WebApplication EhfApp, FakeAccessPoint AccessPoint,
        HttpClient Client, string MigrationConnectionString) : IAsyncDisposable
    {
        private static readonly string[] FinanceTables =
            ["salesinvoiceline", "salesinvoice", "supplierinvoiceline", "supplierinvoice", "bankaccount", "settlementitem", "settlement", "openitem",
             "posting", "journalentry", "voucherline", "voucher", "account"];

        public async ValueTask DisposeAsync()
        {
            foreach (var app in new[] { "ehf", "norway" })
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
                    + "\nDELETE FROM core.number_counter WHERE object_id LIKE 'finance.%' OR object_id LIKE 'norway.%';"
                    + "\nDELETE FROM core.workflow_definition WHERE workflow_id LIKE 'finance.%';",
                    conn);
                await cmd.ExecuteNonQueryAsync();
            }

            var archives = await Client.GetFromJsonAsync<JsonElement>("/api/v1/apps/archives");
            foreach (var archive in archives.EnumerateArray().Where(a => a.GetProperty("appId").GetString() is "finance" or "ehf" or "norway"))
            {
                await Client.PostAsync($"/api/v1/apps/archives/{archive.GetProperty("id").GetGuid()}/purge", null);
            }

            foreach (var app in new[] { "core.currencies", "core.organization", "core.dimensions", "core.parties" })
            {
                await Client.DeleteAsync($"/api/v1/apps/{app}?purge=true");
            }

            Client.Dispose();
            await EhfApp.DisposeAsync();
            await Norway.DisposeAsync();
            await Finance.DisposeAsync();
            await Factory.DisposeAsync();
        }
    }

    // ------------------------------------------------------------------ the format

    private static EhfInvoice SampleInvoice(decimal quantity = 2m) => new(
        "10001", new DateOnly(2026, 3, 10), new DateOnly(2026, 4, 9), "NOK", "REF-1", "1234567897", "12345678903",
        new EhfParty("Demohandel AS", "999888777", "Storgata 1", "0150", "Oslo", "post@demohandel.no", true),
        new EhfParty("Kunde AS", "955777888", null, null, null, null, false),
        [new EhfLine("Konsulent", quantity, 500m, quantity * 500m, 25m), new EhfLine("Frakt", 1m, 100.10m, 100.10m, 15m), new EhfLine("Fritatt", 1m, 50m, 50m, 0m)]);

    private static XmlSchemaSet UblSchemas()
    {
        var set = new XmlSchemaSet { XmlResolver = new System.Xml.XmlUrlResolver() };
        var folder = Path.Combine(System.AppContext.BaseDirectory, "TestData", "ubl");
        // The signature schema starts with a DOCTYPE, which the schema compiler would not read on its own
        using (var dsig = System.Xml.XmlReader.Create(
                   Path.Combine(folder, "common", "UBL-xmldsig-core-schema-2.1.xsd"),
                   new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Parse, XmlResolver = new System.Xml.XmlUrlResolver() }))
        {
            set.Add("http://www.w3.org/2000/09/xmldsig#", dsig);
        }

        set.Add(null, Path.Combine(folder, "maindoc", "UBL-Invoice-2.1.xsd"));
        set.Compile();
        return set;
    }

    private static List<string> Validate(XDocument document)
    {
        var errors = new List<string>();
        document.Validate(UblSchemas(), (_, e) => errors.Add(e.Message));
        return errors;
    }

    [Fact]
    public void AnOutgoingInvoice_IsValidUbl_WithTheBisIdentifiers_AndTotalsPerVatCategory()
    {
        var document = Ubl.Write(SampleInvoice());
        Assert.Empty(Validate(document));

        XNamespace cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
        XNamespace cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
        var root = document.Root!;
        Assert.Equal(Ubl.CustomizationId, root.Element(cbc + "CustomizationID")!.Value);
        Assert.Equal(Ubl.ProfileId, root.Element(cbc + "ProfileID")!.Value);
        Assert.Equal("380", root.Element(cbc + "InvoiceTypeCode")!.Value);
        var seller = root.Element(cac + "AccountingSupplierParty")!.Element(cac + "Party")!;
        Assert.Equal("0192", seller.Element(cbc + "EndpointID")!.Attribute("schemeID")!.Value);
        Assert.Equal("NO999888777MVA", seller.Element(cac + "PartyTaxScheme")!.Element(cbc + "CompanyID")!.Value);

        // 1000.00 at 25 %, 100.10 at 15 %, 50.00 at 0 %: tax 250.00 + 15.02 (15.015 rounded up) + 0
        var tax = root.Element(cac + "TaxTotal")!;
        Assert.Equal("265.02", tax.Element(cbc + "TaxAmount")!.Value);
        var subtotals = tax.Elements(cac + "TaxSubtotal").ToList();
        Assert.Equal(3, subtotals.Count);
        Assert.Equal(["S", "S", "Z"], subtotals.Select(s => s.Element(cac + "TaxCategory")!.Element(cbc + "ID")!.Value));
        var total = root.Element(cac + "LegalMonetaryTotal")!;
        Assert.Equal("1150.10", total.Element(cbc + "LineExtensionAmount")!.Value);
        Assert.Equal("1415.12", total.Element(cbc + "PayableAmount")!.Value);
        Assert.Equal("1234567897", root.Element(cac + "PaymentMeans")!.Element(cbc + "PaymentID")!.Value);
    }

    [Fact]
    public void AnInvoiceThatLacksWhatBisRequires_IsRefused_InPlainWords()
    {
        var good = SampleInvoice();
        var cases = new (EhfInvoice Invoice, string Expected)[]
        {
            (good with { BuyerReference = "" }, "kundens referanse"),
            (good with { Buyer = good.Buyer with { OrganizationNumber = "" } }, "kunden mangler organisasjonsnummer"),
            (good with { Seller = good.Seller with { City = null } }, "selskapets adresse"),
            (good with { Seller = good.Seller with { VatRegistered = false } }, "MVA-registrert"),
            (good with { Lines = [good.Lines[0] with { VatRate = null }] }, "MVA-kode"),
            (good with { Lines = [good.Lines[0] with { Amount = -10m }] }, "kreditnota")
        };
        foreach (var (invoice, expected) in cases)
        {
            var ex = Assert.Throws<EhfException>(() => Ubl.Write(invoice));
            Assert.Contains(expected, ex.Message);
        }
    }

    [Fact]
    public void AnIncomingInvoice_IsRead_AndACreditNoteIsNegative()
    {
        var read = Ubl.Read(Encoding.UTF8.GetBytes(Ubl.Write(SampleInvoice()).ToString()));
        Assert.Equal("Demohandel AS", read.SupplierName);
        Assert.Equal("999888777", read.SupplierOrgNumber);
        Assert.Equal("10001", read.InvoiceNumber);
        Assert.Equal(new DateOnly(2026, 4, 9), read.DueDate);
        Assert.Equal(1415.12m, read.TotalAmount);
        Assert.Equal("1234567897", read.PaymentReference);
        Assert.Equal("12345678903", read.BankAccount);
        Assert.Equal(3, read.Lines.Count);
        Assert.Equal(1415.12m, read.Lines.Sum(l => l.Amount + l.VatAmount)); // the lines add up to the document's total

        var credit = Ubl.Read(Encoding.UTF8.GetBytes("""
            <CreditNote xmlns="urn:oasis:names:specification:ubl:schema:xsd:CreditNote-2" xmlns:cac="urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2" xmlns:cbc="urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2">
              <cbc:ID>K-1</cbc:ID><cbc:IssueDate>2026-03-11</cbc:IssueDate><cbc:DocumentCurrencyCode>NOK</cbc:DocumentCurrencyCode>
              <cac:AccountingSupplierParty><cac:Party><cac:PartyLegalEntity><cbc:RegistrationName>Lev AS</cbc:RegistrationName><cbc:CompanyID schemeID="0192">944555666</cbc:CompanyID></cac:PartyLegalEntity></cac:Party></cac:AccountingSupplierParty>
              <cac:TaxTotal><cbc:TaxAmount currencyID="NOK">25.00</cbc:TaxAmount></cac:TaxTotal>
              <cac:LegalMonetaryTotal><cbc:PayableAmount currencyID="NOK">125.00</cbc:PayableAmount></cac:LegalMonetaryTotal>
              <cac:CreditNoteLine><cbc:ID>1</cbc:ID><cbc:LineExtensionAmount currencyID="NOK">100.00</cbc:LineExtensionAmount>
                <cac:Item><cbc:Name>Retur</cbc:Name><cac:ClassifiedTaxCategory><cbc:ID>S</cbc:ID><cbc:Percent>25</cbc:Percent></cac:ClassifiedTaxCategory></cac:Item></cac:CreditNoteLine>
            </CreditNote>
            """));
        Assert.Equal(-125m, credit.TotalAmount);
        Assert.Equal(-100m, credit.Lines.Single().Amount);
        Assert.Equal(-25m, credit.Lines.Single().VatAmount);

        Assert.Throws<EhfException>(() => Ubl.Read(Encoding.UTF8.GetBytes("<foo/>")));
        Assert.Throws<EhfException>(() => Ubl.Read(Encoding.UTF8.GetBytes("ikke xml")));
    }

    // ------------------------------------------------------------------ the whole way

    [Fact]
    public async Task ASalesInvoice_IsPosted_SentAsValidEhf_AndAReceivedEhfBecomesAProposal()
    {
        await using var env = await SetUpAsync();
        var c = env.Client;
        var a = await AccountsAsync(c);
        var codes = (await QueryAsync(c, "norway.vatcode", "code ne ''", 100)).ToDictionary(x => Field(x, "code")!);
        await CreateAsync(c, "finance.bankaccount", new
        {
            name = "Driftskonto", accountNumber = "12345678903", holderName = "Demohandel AS", ledgerAccount = Id(a["1920"]), currency = "NOK"
        });
        var settings = (await QueryAsync(c, "norway.settings", "singleton eq 'settings'")).Single();
        using (var set = await PatchAsync(c, "norway.settings", settings, new Dictionary<string, object?>
               {
                   ["companyName"] = "Demohandel AS", ["organizationNumber"] = "999888777", ["street"] = "Storgata 1", ["postalCode"] = "0150", ["city"] = "Oslo",
                   ["email"] = "post@demohandel.no", ["vatRegistered"] = true
               }))
        {
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        }

        var customer = await CreateAsync(c, "core.party", new Dictionary<string, object?>
        {
            ["partyNumber"] = "K-1", ["name"] = "Kunde AS", ["partyType"] = "organization", ["role"] = "customer", ["organizationNumber"] = "955777888"
        });

        // 1. A draft with two lines: the amounts and the VAT follow from quantity, price and VAT code
        async Task<JsonElement> NewInvoiceAsync()
        {
            var invoice = await CreateAsync(c, "finance.salesinvoice", new
            {
                customer = Id(customer), invoiceDate = "2026-03-10", dueDate = "2026-04-09", buyerReference = "REF-1"
            });
            await CreateAsync(c, "finance.salesinvoiceline", new Dictionary<string, object?>
            {
                ["invoice"] = Id(invoice), ["description"] = "Konsulent", ["quantity"] = 2, ["unitPrice"] = 500, ["account"] = Id(a["3000"]), ["norway.vatCode"] = Id(codes["3"])
            });
            await CreateAsync(c, "finance.salesinvoiceline", new Dictionary<string, object?>
            {
                ["invoice"] = Id(invoice), ["description"] = "Frakt", ["quantity"] = 1, ["unitPrice"] = 100.10, ["account"] = Id(a["3000"]), ["norway.vatCode"] = Id(codes["31"])
            });
            return await GetAsync(c, "finance.salesinvoice", Id(invoice));
        }

        var invoice = await NewInvoiceAsync();
        Assert.Matches(@"^\d{5}$|^\d{4}-\d{5}$|\d{5}", Field(invoice, "invoiceNumber"));
        var lines = await QueryAsync(c, "finance.salesinvoiceline", $"invoice eq '{Id(invoice)}'");
        Assert.Equal(1000m, Decimal(lines.Single(l => Field(l, "description") == "Konsulent"), "amount"));
        Assert.Equal(250m, Decimal(lines.Single(l => Field(l, "description") == "Konsulent"), "vatAmount"));
        Assert.Equal(15.02m, Decimal(lines.Single(l => Field(l, "description") == "Frakt"), "vatAmount"));

        // An input code is refused on a sales line
        using (var wrong = await c.PostAsJsonAsync(Url("finance.salesinvoiceline"), new Dictionary<string, object?>
               {
                   ["invoice"] = Id(invoice), ["description"] = "x", ["quantity"] = 1, ["unitPrice"] = 1, ["account"] = Id(a["3000"]), ["norway.vatCode"] = Id(codes["1"])
               }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
            Assert.Contains("inngående", await wrong.Content.ReadAsStringAsync());
        }

        // 2. Posting makes the voucher with the receivable, and the KID is on the invoice
        Assert.Equal("posting", Field(await TransitionAsync(c, "finance.salesinvoice", invoice, "post"), "status"));
        invoice = await WaitForStatusAsync(c, "finance.salesinvoice", Id(invoice), "posted");
        Assert.Equal(1365.12m, Decimal(invoice, "totalAmount"));
        Assert.True(Kid.IsValid(Field(invoice, "paymentReference")), Field(invoice, "paymentReference"));
        var entry = (await QueryAsync(c, "finance.journalentry", $"voucher eq '{Field(invoice, "voucher")}'")).Single();
        var postings = await QueryAsync(c, "finance.posting", $"entry eq '{Id(entry)}'");
        Assert.Equal(1365.12m, Decimal(postings.Single(p => Guid.Parse(Field(p, "account")!) == Id(a["1500"])), "debit"));
        Assert.Equal(1100.10m, postings.Where(p => Guid.Parse(Field(p, "account")!) == Id(a["3000"])).Sum(p => Decimal(p, "credit")));
        Assert.Equal(265.12m - 0.10m, postings.Where(p => Guid.Parse(Field(p, "account")!) == Id(a["2700"])).Sum(p => Decimal(p, "credit")));
        using (var locked = await PatchAsync(c, "finance.salesinvoiceline", lines[0], new { unitPrice = 1 }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, locked.StatusCode);
        }

        // 3. Sending hands a valid EHF to the access point, addressed by organisation number
        await TransitionAsync(c, "finance.salesinvoice", invoice, "send");
        var sent = await WaitForStatusAsync(c, "finance.salesinvoice", Id(invoice), "sent");
        var delivered = Assert.Single(env.AccessPoint.Sent);
        Assert.Equal("0192:999888777", delivered.SenderId);
        Assert.Equal("0192:955777888", delivered.ReceiverId);
        Assert.Equal("AP-1", Field(sent, "deliveryReference"));
        var xml = XDocument.Parse(Encoding.UTF8.GetString(delivered.Xml));
        Assert.Empty(Validate(xml));
        Assert.Equal(Field(invoice, "paymentReference"), xml.Descendants().Single(e => e.Name.LocalName == "PaymentID").Value);
        Assert.Equal(Encoding.UTF8.GetString(delivered.Xml), Encoding.UTF8.GetString(await c.GetByteArrayAsync($"/api/v1/files/{Field(sent, "file")}/content")));

        // 4. An access point that is down fails the invoice with the reason; sending again works once it is back
        var second = await NewInvoiceAsync();
        await TransitionAsync(c, "finance.salesinvoice", second, "post");
        await WaitForStatusAsync(c, "finance.salesinvoice", Id(second), "posted");
        env.AccessPoint.FailWith = "Aksesspunktet svarer ikke.";
        await TransitionAsync(c, "finance.salesinvoice", await GetAsync(c, "finance.salesinvoice", Id(second)), "send");
        var failed = await WaitForStatusAsync(c, "finance.salesinvoice", Id(second), "failed");
        Assert.Contains("svarer ikke", Field(failed, "deliveryMessage"));
        env.AccessPoint.FailWith = null;
        await TransitionAsync(c, "finance.salesinvoice", failed, "retry");
        await WaitForStatusAsync(c, "finance.salesinvoice", Id(second), "sent");
        Assert.Equal(2, env.AccessPoint.Sent.Count);

        // 5. A document waiting at the access point becomes a proposal, found by organisation number and coded by its VAT rate
        await CreateAsync(c, "core.party", new Dictionary<string, object?>
        {
            ["partyNumber"] = "L-B", ["name"] = "Leverandør B AS", ["partyType"] = "organization", ["role"] = "supplier", ["organizationNumber"] = "944555666"
        });
        var incoming = Ubl.Write(new EhfInvoice(
            "B-77", new DateOnly(2026, 3, 12), new DateOnly(2026, 4, 12), "NOK", "REF", "1234567897", "98765432109",
            new EhfParty("Leverandør B AS", "944555666", "Veien 2", "5003", "Bergen", null, true),
            new EhfParty("Demohandel AS", "999888777", null, null, null, null, false),
            [new EhfLine("Varer", 1m, 800m, 800m, 25m)]));
        env.AccessPoint.Inbox.Add(new ReceivedDocument("doc-1", Encoding.UTF8.GetBytes(incoming.ToString())));
        env.AccessPoint.Inbox.Add(new ReceivedDocument("doc-2", Encoding.UTF8.GetBytes("<ikke-en-faktura/>")));
        await RunReceiveAsync(c);

        var invoices = await QueryAsync(c, "finance.supplierinvoice", "extractor eq 'ehf'");
        Assert.Equal(2, invoices.Count);
        var proposal = invoices.Single(i => Field(i, "invoiceNumber") == "B-77");
        Assert.Equal("proposed", Field(proposal, "status"));
        Assert.Equal(Id((await QueryAsync(c, "core.party", "organizationNumber eq '944555666'")).Single()), Guid.Parse(Field(proposal, "supplier")!));
        Assert.Equal(1000m, Decimal(proposal, "totalAmount"));
        var line = (await QueryAsync(c, "finance.supplierinvoiceline", $"invoice eq '{Id(proposal)}'")).Single();
        Assert.Equal(200m, Decimal(line, "vatAmount"));
        Assert.Equal("1", Field(await GetAsync(c, "norway.vatcode", Guid.Parse(Field(line, "norway.vatCode")!)), "code"));
        var broken = invoices.Single(i => Field(i, "status") == "failed");
        Assert.Contains("kunne ikke leses", Field(broken, "message"));
        Assert.Equal(["doc-1", "doc-2"], env.AccessPoint.Acknowledged.Order());

        // The same document again adds nothing
        env.AccessPoint.Acknowledged.Clear();
        env.AccessPoint.Inbox.Add(new ReceivedDocument("doc-3", Encoding.UTF8.GetBytes(incoming.ToString())));
        await RunReceiveAsync(c);
        Assert.Equal(2, (await QueryAsync(c, "finance.supplierinvoice", "extractor eq 'ehf'")).Count);
        Assert.Contains("doc-3", env.AccessPoint.Acknowledged);
    }

    [Fact]
    public async Task WithoutAnAccessPoint_NothingIsSent_AndTheInvoiceFailsWithTheReason()
    {
        await using var env = await SetUpAsync(configured: false);
        var c = env.Client;
        var a = await AccountsAsync(c);
        var codes = (await QueryAsync(c, "norway.vatcode", "code ne ''", 100)).ToDictionary(x => Field(x, "code")!);
        var customer = await CreateAsync(c, "core.party", new Dictionary<string, object?>
        {
            ["partyNumber"] = "K-2", ["name"] = "Kunde 2 AS", ["partyType"] = "organization", ["role"] = "customer", ["organizationNumber"] = "955777888"
        });
        var settings = (await QueryAsync(c, "norway.settings", "singleton eq 'settings'")).Single();
        using (await PatchAsync(c, "norway.settings", settings, new Dictionary<string, object?>
               {
                   ["companyName"] = "Demohandel AS", ["organizationNumber"] = "999888777", ["street"] = "Storgata 1", ["postalCode"] = "0150", ["city"] = "Oslo"
               }))
        {
        }

        var invoice = await CreateAsync(c, "finance.salesinvoice", new { customer = Id(customer), invoiceDate = "2026-03-10", buyerReference = "R" });
        await CreateAsync(c, "finance.salesinvoiceline", new Dictionary<string, object?>
        {
            ["invoice"] = Id(invoice), ["description"] = "Vare", ["quantity"] = 1, ["unitPrice"] = 100, ["account"] = Id(a["3000"]), ["norway.vatCode"] = Id(codes["3"])
        });
        await TransitionAsync(c, "finance.salesinvoice", invoice, "post");
        await WaitForStatusAsync(c, "finance.salesinvoice", Id(invoice), "posted");
        await TransitionAsync(c, "finance.salesinvoice", await GetAsync(c, "finance.salesinvoice", Id(invoice)), "send");
        var failed = await WaitForStatusAsync(c, "finance.salesinvoice", Id(invoice), "failed");
        Assert.Contains("ikke satt opp noe PEPPOL-aksesspunkt", Field(failed, "deliveryMessage"));
    }

    // ------------------------------------------------------------------ helpers

    private static async Task RunReceiveAsync(HttpClient c)
    {
        using var run = await c.PostAsync("/api/v1/schedules/ehf/ehf.receive.regularly/run", null);
        Assert.True(run.IsSuccessStatusCode, await run.Content.ReadAsStringAsync());
    }

    private static async Task<JsonElement> WaitForStatusAsync(HttpClient c, string objectId, Guid id, string status) =>
        (await WaitForAsync(async () =>
        {
            var current = await GetAsync(c, objectId, id);
            return Field(current, "status") == status ? [current] : null;
        }))[0];

    private async Task<Env> SetUpAsync(bool configured = true)
    {
        var accessPoint = new FakeAccessPoint();
        var factory = CreateFactory();
        var finance = await StartContainerAsync<FinanceApp>(factory);
        var norway = await StartContainerAsync<NorwayApp>(factory);
        var ehf = await StartContainerAsync<Ehf.EhfApp>(factory, services =>
        {
            if (configured)
            {
                services.AddSingleton<IAccessPoint>(accessPoint);
            }
            else
            {
                services.AddSingleton<IAccessPoint, NotConfiguredAccessPoint>();
            }
        });
        var orchestrator = factory.Services.GetRequiredService<KubernetesAppOrchestrator>();
        orchestrator.RegisterEndpoint("finance", "1.0.0", "http://finance:50051", () => finance.GetTestServer().CreateHandler());
        orchestrator.RegisterEndpoint("norway", "1.0.0", "http://norway:50051", () => norway.GetTestServer().CreateHandler());
        orchestrator.RegisterEndpoint("ehf", "1.0.0", "http://ehf:50051", () => ehf.GetTestServer().CreateHandler());

        var client = factory.CreateClient();
        using (var res = await client.PostAsJsonAsync("/api/v1/auth/dev-login", new { }))
        {
            res.EnsureSuccessStatusCode();
            var token = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("accessToken").GetString();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var env = new Env(factory, finance, norway, ehf, accessPoint, client, fixture.MigrationConnectionString);
        foreach (var app in new[] { "core.parties", "core.dimensions", "core.organization", "core.currencies", "finance", "norway", "ehf" })
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

    private static async Task<WebApplication> StartContainerAsync<TApp>(WebApplicationFactory<Program> core, Action<IServiceCollection>? configure = null) where TApp : AppBase
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        configure?.Invoke(builder.Services);
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
