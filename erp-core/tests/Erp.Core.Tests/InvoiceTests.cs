using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Erp.Core.Domain.Entities;
using Erp.Core.Domain.Tenancy;
using Erp.Core.Infrastructure.Apps;
using Erp.Core.Infrastructure.Documents;
using Erp.Core.Infrastructure.Persistence;
using Erp.Core.Infrastructure.Storage;
using Erp.Sdk;
using Finance;
using Invoice.Claude;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Norway;
using Npgsql;
using Xunit;

namespace Erp.Core.Tests;

/// <summary>Supplier invoices: a scanned document in the inbox becomes a proposal, goes through approval, is posted, and is paid.</summary>
[Collection(PostgresDatabaseCollection.Name)]
public sealed class InvoiceTests(PostgresDatabaseFixture fixture)
{
    private const string TestOidcSigningKey = "test-only-external-oidc-signing-key-at-least-32-bytes!";
    private const string TestOidcIssuer = "https://oidc.test.local/realms/erp-dev";
    private const string TestOidcAudience = "erp-web";

    /// <summary>Stands in for the AI: answers by a marker in the document text, or fails when asked to.</summary>
    private sealed class FakeExtractor : IInvoiceExtractor
    {
        public Dictionary<string, ExtractedInvoice> Answers { get; } = [];
        public string? FailWith { get; set; }
        public string Name => "fake";

        public Task<ExtractedInvoice> ExtractAsync(byte[] content, string contentType, string fileName, CancellationToken cancellationToken)
        {
            if (FailWith is { } reason)
            {
                throw new InvoiceExtractionException(reason);
            }

            var text = Encoding.UTF8.GetString(content);
            return Task.FromResult(Answers.First(a => text.Contains(a.Key)).Value);
        }
    }

    private sealed record Env(
        WebApplicationFactory<Program> Factory, WebApplication Finance, WebApplication Norway, WebApplication Reader, FakeExtractor Fake,
        HttpClient Client, HttpClient Approver, string ApproverSubject, string MigrationConnectionString) : IAsyncDisposable
    {
        private static readonly string[] FinanceTables =
            ["supplierinvoiceline", "supplierinvoice", "payment", "paymentbatch", "bankaccount", "settlementitem", "settlement", "openitem", "posting",
             "journalentry", "voucherline", "voucher", "account"];

        public async ValueTask DisposeAsync()
        {
            foreach (var app in new[] { "invoice.claude", "norway" })
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
                    + "\nDELETE FROM core.workflow_definition WHERE workflow_id LIKE 'finance.%';"
                    + "\nUPDATE core.task SET status = 'cancelled' WHERE status IN ('open', 'waiting');"
                    + "\nUPDATE core.workflow_instance SET status = 'rejected', completed_at = now() WHERE status = 'running';"
                    + $"\nDELETE FROM core.\"user\" WHERE external_subject = '{ApproverSubject}';",
                    conn);
                await cmd.ExecuteNonQueryAsync();
            }

            var archives = await Client.GetFromJsonAsync<JsonElement>("/api/v1/apps/archives");
            foreach (var archive in archives.EnumerateArray().Where(a => a.GetProperty("appId").GetString() is "finance" or "invoice.claude" or "norway"))
            {
                await Client.PostAsync($"/api/v1/apps/archives/{archive.GetProperty("id").GetGuid()}/purge", null);
            }

            foreach (var app in new[] { "core.currencies", "core.organization", "core.dimensions", "core.parties" })
            {
                await Client.DeleteAsync($"/api/v1/apps/{app}?purge=true");
            }

            Approver.Dispose();
            Client.Dispose();
            await Reader.DisposeAsync();
            await Norway.DisposeAsync();
            await Finance.DisposeAsync();
            await Factory.DisposeAsync();
        }
    }

    // ------------------------------------------------------------------ the whole way

    [Fact]
    public async Task AScannedInvoice_GoesFromTheInbox_ThroughReviewAndApproval_ToAPostedVoucher_AndAPayment()
    {
        await using var env = await SetUpAsync();
        var c = env.Client;
        var a = await AccountsAsync(c);
        var run = Guid.NewGuid().ToString("N");
        var supplier = await CreateAsync(c, "core.party", new Dictionary<string, object?>
        {
            ["partyNumber"] = "L-A", ["name"] = "Leverandør A AS", ["partyType"] = "organization", ["role"] = "supplier",
            ["organizationNumber"] = "955777888", ["finance.bankAccount"] = "98765432109"
        });
        var bank = await CreateAsync(c, "finance.bankaccount", new
        {
            name = "Driftskonto", accountNumber = "12345678903", holderName = "Demohandel AS", ledgerAccount = Id(a["1920"]), currency = "NOK"
        });
        env.Fake.Answers[$"A-100 {run}"] = Read("A-100", "1234567897", 1000m, 25m, 250m);
        env.Fake.Answers[$"A-101 {run}"] = Read("A-101", "1234567890", 400m, 25m, 100m);

        // 1. A PDF arrives in the inbox: an invoice is made, read, and proposed with the supplier found by organisation number
        var invoice = await IngestAsync(c, env, $"A-100 {run}");
        Assert.Equal("proposed", Field(invoice, "status"));
        Assert.Equal("fake", Field(invoice, "extractor"));
        Assert.Equal(Id(supplier), Guid.Parse(Field(invoice, "supplier")!));
        Assert.Equal("A-100", Field(invoice, "invoiceNumber"));
        Assert.Equal(1250m, Decimal(invoice, "totalAmount"));
        var line = (await QueryAsync(c, "finance.supplierinvoiceline", $"invoice eq '{Id(invoice)}'")).Single();
        Assert.Equal(1000m, Decimal(line, "amount"));
        Assert.Null(Field(line, "account")); // nothing earlier to learn from
        var vatCode = await GetAsync(c, "norway.vatcode", Guid.Parse(Field(line, "norway.vatCode")!)); // proposed from the 25 % in the document
        Assert.Equal("1", Field(vatCode, "code"));
        var item = (await c.GetFromJsonAsync<JsonElement>("/api/v1/inbox/items?top=200")).EnumerateArray()
            .Single(i => i.GetProperty("id").GetGuid() == Guid.Parse(Field(invoice, "inboxItem")!));
        Assert.Equal("processed", item.GetProperty("status").GetString());

        // 2. It cannot go to approval before every line has an account
        using (var refused = await c.PostAsync($"{Url("finance.supplierinvoice")}/{Id(invoice)}/transitions/submit", null))
        {
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("Alle linjer må ha konto", await refused.Content.ReadAsStringAsync());
        }

        using (var coded = await PatchAsync(c, "finance.supplierinvoiceline", line, new { account = Id(a["6300"]) }))
        {
            Assert.Equal(HttpStatusCode.OK, coded.StatusCode);
        }

        var pending = await TransitionAsync(c, "finance.supplierinvoice", invoice, "submit");
        Assert.Equal("pending", Field(pending, "status"));
        using (var locked = await PatchAsync(c, "finance.supplierinvoiceline", line, new { amount = 5m }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, locked.StatusCode);
        }

        // 3. The approver approves; posting is still a deliberate step
        await ApproveNextTaskAsync(env.Approver);
        var approved = await GetAsync(c, "finance.supplierinvoice", Id(invoice));
        Assert.Equal("approved", Field(approved, "status"));
        Assert.Null(Field(approved, "voucher"));
        var posted = await TransitionAsync(c, "finance.supplierinvoice", approved, "post");
        Assert.Equal("posted", Field(posted, "status"));

        var entry = (await QueryAsync(c, "finance.journalentry", $"voucher eq '{Field(await GetAsync(c, "finance.supplierinvoice", Id(invoice)), "voucher")}'")).Single();
        var postings = await QueryAsync(c, "finance.posting", $"entry eq '{Id(entry)}'");
        Assert.Equal(3, postings.Count);
        Assert.Equal(1000m, Decimal(postings.Single(p => Guid.Parse(Field(p, "account")!) == Id(a["6300"])), "debit"));
        Assert.Equal(250m, Decimal(postings.Single(p => Guid.Parse(Field(p, "account")!) == Id(a["2710"])), "debit")); // VAT by the Norway package
        var credit = postings.Single(p => Guid.Parse(Field(p, "account")!) == Id(a["2400"]));
        Assert.Equal(1250m, Decimal(credit, "credit"));
        Assert.Equal(Id(supplier), Guid.Parse(Field(credit, "party")!));
        var open = (await QueryAsync(c, "finance.openitem", $"posting eq '{Id(credit)}'")).Single();
        Assert.Equal("1234567897", Field(open, "paymentReference"));

        // 4. The supplier item is paid through the bank contract, with the invoice's KID
        var batch = await CreateAsync(c, "finance.paymentbatch", new
        {
            name = "Betaling", bankAccount = Id(bank), executionDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(3).ToString("yyyy-MM-dd"), dueUntil = "2026-04-30"
        });
        var payment = (await QueryAsync(c, "finance.payment", $"batch eq '{Id(batch)}'")).Single();
        Assert.Equal(1250m, Decimal(payment, "amount"));
        Assert.Equal("1234567897", Field(payment, "paymentReference"));
        Assert.Equal("98765432109", Field(payment, "creditorAccount"));

        // 5. A second invoice from the same supplier is coded like the first, and the checks hold
        var second = await IngestAsync(c, env, $"A-101 {run}");
        var secondLine = (await QueryAsync(c, "finance.supplierinvoiceline", $"invoice eq '{Id(second)}'")).Single();
        Assert.Equal(Id(a["6300"]), Guid.Parse(Field(secondLine, "account")!));
        Assert.Equal(Id(a["6300"]), Guid.Parse(Field(secondLine, "suggestedAccount")!));
        using (var badKid = await c.PostAsync($"{Url("finance.supplierinvoice")}/{Id(second)}/transitions/submit", null))
        {
            Assert.Equal(HttpStatusCode.BadRequest, badKid.StatusCode);
            Assert.Contains("gyldig KID", await badKid.Content.ReadAsStringAsync());
        }

        env.Fake.Answers[$"A-100 {run}"] = Read("A-100", null, 1000m, 25m, 250m);
        var duplicate = await IngestAsync(c, env, $"A-100 {run} again");
        using (var twice = await c.PostAsync($"{Url("finance.supplierinvoice")}/{Id(duplicate)}/transitions/submit", null))
        {
            Assert.Equal(HttpStatusCode.BadRequest, twice.StatusCode);
            Assert.Contains("registrert fra før", await twice.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task ADocumentThatCannotBeRead_IsFailedWithTheReason_AndCanBeReadAgain_AndADifferentBankAccountIsStopped()
    {
        await using var env = await SetUpAsync();
        var c = env.Client;
        var a = await AccountsAsync(c);
        var run = Guid.NewGuid().ToString("N");
        var supplier = await CreateAsync(c, "core.party", new Dictionary<string, object?>
        {
            ["partyNumber"] = "L-B", ["name"] = "Leverandør B AS", ["partyType"] = "organization", ["role"] = "supplier",
            ["organizationNumber"] = "944555666", ["finance.bankAccount"] = "55555555555"
        });
        env.Fake.Answers[$"B-1 {run}"] = Read("B-1", "1234567897", 800m, 0m, 0m, "944555666", "Leverandør B AS", "98765432109");

        // The extractor is down: the invoice fails with the reason, and shows up as work to do
        env.Fake.FailWith = "Tolketjenesten er utilgjengelig.";
        var failed = await IngestAsync(c, env, $"B-1 {run}", "failed");
        Assert.Contains("utilgjengelig", Field(failed, "message"));
        Assert.Null(Field(failed, "extractor"));
        var attention = await c.GetFromJsonAsync<JsonElement>("/api/v1/attention");
        Assert.Contains(attention.EnumerateArray(), i => i.GetProperty("id").GetString() == "finance.supplierinvoices.failed");

        // Reading again works once the service is back
        env.Fake.FailWith = null;
        await TransitionAsync(c, "finance.supplierinvoice", failed, "retry");
        var again = (await WaitForAsync(async () =>
        {
            var current = await GetAsync(c, "finance.supplierinvoice", Id(failed));
            return Field(current, "status") == "proposed" ? [current] : null;
        }))[0];
        Assert.Equal(Id(supplier), Guid.Parse(Field(again, "supplier")!));
        Assert.Null(Field(again, "message"));

        // The account on the invoice is not the one registered for the supplier: a person has to settle it
        var line = (await QueryAsync(c, "finance.supplierinvoiceline", $"invoice eq '{Id(again)}'")).Single();
        using (var coded = await PatchAsync(c, "finance.supplierinvoiceline", line, new { account = Id(a["6300"]) }))
        {
            Assert.Equal(HttpStatusCode.OK, coded.StatusCode);
        }

        using (var stopped = await c.PostAsync($"{Url("finance.supplierinvoice")}/{Id(again)}/transitions/submit", null))
        {
            Assert.Equal(HttpStatusCode.BadRequest, stopped.StatusCode);
            Assert.Contains("registrerte hos leverandøren", await stopped.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task AnInboxItemWithoutAnInvoiceDocument_IsLeftForTheManualWorkList()
    {
        await using var env = await SetUpAsync();
        var c = env.Client;
        var (inboxId, _) = await CreateInboxAsync(c);
        var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(Encoding.UTF8.GetBytes("notat " + Guid.NewGuid())) { Headers = { ContentType = new MediaTypeHeaderValue("text/plain") } }, "file", "notat.txt" },
            { new StringContent("Bare et notat"), "subject" }
        };
        using var created = await c.PostAsync($"/api/v1/inboxes/{inboxId}/items", form);
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        await Task.Delay(1500);
        Assert.Empty(await QueryAsync(c, "finance.supplierinvoice", "invoiceNumber ne ''"));
        Assert.Empty(await QueryAsync(c, "finance.supplierinvoice", "status eq 'received'"));
    }

    // ------------------------------------------------------------------ the extractor

    [Fact]
    public void TheModelsAnswer_IsReadAsData_WhateverItIsWrappedIn()
    {
        var read = InvoiceJson.Parse("""
            Her er svaret:
            ```json
            {"supplierName":" Leverandør AS ","supplierOrgNumber":"NO 955 777 888 MVA","invoiceNumber":"F-77","invoiceDate":"2026-03-10","dueDate":"10.04.2026",
             "currency":"nok","totalAmount":"1 250.00","vatAmount":250,"paymentReference":"1234 5678 97","bankAccount":"9876 54 32109",
             "lines":[{"description":"Leie","amount":1000,"vatRate":25,"vatAmount":250},{"description":"uten beløp"}]}
            ```
            """);
        Assert.Equal("Leverandør AS", read.SupplierName);
        Assert.Equal("955777888", read.SupplierOrgNumber);
        Assert.Equal(new DateOnly(2026, 3, 10), read.InvoiceDate);
        Assert.Null(read.DueDate); // a date that is not ISO is not guessed at
        Assert.Equal("NOK", read.Currency);
        Assert.Null(read.TotalAmount); // "1 250.00" is not a plain number
        Assert.Equal("1234567897", read.PaymentReference);
        Assert.Equal("98765432109", read.BankAccount);
        var line = Assert.Single(read.Lines); // the line without an amount is dropped
        Assert.Equal(1000m, line.Amount);

        // Without lines, one is made from the totals
        var single = InvoiceJson.Parse("""{"totalAmount":1250,"vatAmount":250}""");
        Assert.Equal(1000m, single.Lines.Single().Amount);
        Assert.Equal(25m, single.Lines.Single().VatRate);

        Assert.Throws<InvoiceExtractionException>(() => InvoiceJson.Parse("beklager, jeg kan ikke lese dette"));
    }

    [Fact]
    public async Task Claude_IsCalledOnTheEuEndpointOnly_WithTheDocument_AndItsAnswerIsRead()
    {
        var handler = new RecordingHandler("""
            {"output":{"message":{"role":"assistant","content":[{"text":"{\"invoiceNumber\":\"X-1\",\"totalAmount\":125.5,\"lines\":[{\"description\":\"d\",\"amount\":100.4,\"vatRate\":25,\"vatAmount\":25.1}]}"}]}},"stopReason":"end_turn"}
            """);
        var options = Options.Create(new ClaudeOptions { ModelId = "eu.anthropic.test-model-v1:0", ApiKey = "secret-key" });
        var extractor = new ClaudeInvoiceExtractor(new HttpClient(handler), options);

        var read = await extractor.ExtractAsync(Encoding.UTF8.GetBytes("%PDF-1.4 test"), "application/pdf", "f.pdf", CancellationToken.None);
        Assert.Equal("X-1", read.InvoiceNumber);
        Assert.Equal(125.5m, read.TotalAmount);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("bedrock-runtime.eu-central-1.amazonaws.com", request.Uri.Host);
        Assert.Equal("/model/eu.anthropic.test-model-v1%3A0/converse", request.Uri.AbsolutePath.Replace("%3a", "%3A"));
        Assert.Equal("Bearer secret-key", request.Authorization);
        using var body = JsonDocument.Parse(request.Body);
        var document = body.RootElement.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("document");
        Assert.Equal("pdf", document.GetProperty("format").GetString());
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("%PDF-1.4 test")), document.GetProperty("source").GetProperty("bytes").GetString());
        Assert.Contains("untrusted", body.RootElement.GetProperty("system")[0].GetProperty("text").GetString());

        // Anything but an HTTPS Bedrock host in an EU region is refused before a request is made
        foreach (var endpoint in new[] { "https://bedrock-runtime.us-east-1.amazonaws.com", "http://bedrock-runtime.eu-central-1.amazonaws.com", "https://api.anthropic.com", "https://bedrock-runtime.eu-central-1.amazonaws.com.evil.test" })
        {
            var refused = new ClaudeInvoiceExtractor(new HttpClient(handler), Options.Create(new ClaudeOptions { Endpoint = endpoint, ModelId = "m", ApiKey = "k" }));
            var ex = await Assert.ThrowsAsync<InvoiceExtractionException>(() => refused.ExtractAsync([1], "application/pdf", "f.pdf", CancellationToken.None));
            Assert.Contains("EU-region", ex.Message);
        }

        Assert.Single(handler.Requests);

        // Not configured: a clear message, no request
        var unset = new ClaudeInvoiceExtractor(new HttpClient(handler), Options.Create(new ClaudeOptions()));
        var missing = await Assert.ThrowsAsync<InvoiceExtractionException>(() => unset.ExtractAsync([1], "application/pdf", "f.pdf", CancellationToken.None));
        Assert.Contains("ikke satt opp", missing.Message);

        // A service error is reported, not swallowed
        var failing = new ClaudeInvoiceExtractor(new HttpClient(new RecordingHandler("{}", HttpStatusCode.TooManyRequests)), options);
        var limited = await Assert.ThrowsAsync<InvoiceExtractionException>(() => failing.ExtractAsync([1], "image/png", "f.png", CancellationToken.None));
        Assert.Contains("429", limited.Message);
    }

    private sealed record RecordedRequest(Uri Uri, string? Authorization, string Body);

    private sealed class RecordingHandler(string response, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(request.RequestUri!, request.Headers.Authorization?.ToString(), await request.Content!.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(status) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }

    // ------------------------------------------------------------------ helpers

    private static ExtractedInvoice Read(
        string number, string? kid, decimal net, decimal rate, decimal vat, string org = "955777888", string name = "Leverandør A AS", string account = "98765432109") =>
        new(name, org, number, new DateOnly(2026, 3, 10), new DateOnly(2026, 4, 14), "NOK", net + vat, vat, kid, account,
            [new ExtractedLine("Leie av lokaler", net, rate == 0 ? null : rate, vat)]);

    /// <summary>Puts a PDF in an upload inbox and waits for the invoice made from it to reach the given status.</summary>
    private static async Task<JsonElement> IngestAsync(HttpClient c, Env env, string text, string until = "proposed")
    {
        var (inboxId, _) = await CreateInboxAsync(c);
        var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(Encoding.UTF8.GetBytes($"%PDF-1.4 {text} {Guid.NewGuid()}")) { Headers = { ContentType = new MediaTypeHeaderValue("application/pdf") } }, "file", "faktura.pdf" },
            { new StringContent($"Faktura {text}"), "subject" }
        };
        using var created = await c.PostAsync($"/api/v1/inboxes/{inboxId}/items", form);
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var itemId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        return (await WaitForAsync(async () =>
        {
            var found = await QueryAsync(c, "finance.supplierinvoice", $"inboxItem eq '{itemId}'");
            return found.Count == 1 && Field(found[0], "status") == until ? found : null;
        }))[0];
    }

    private static async Task<(Guid Id, string Address)> CreateInboxAsync(HttpClient c)
    {
        using var res = await c.PostAsJsonAsync("/api/v1/inboxes", new { channel = "upload", name = $"Fakturaer {Guid.NewGuid():N}"[..20] });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var inbox = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        return (inbox.GetProperty("id").GetGuid(), string.Empty);
    }

    private static async Task ApproveNextTaskAsync(HttpClient approver)
    {
        var tasks = await WaitForAsync(async () =>
        {
            var found = await approver.GetFromJsonAsync<JsonElement>("/api/v1/tasks?mine=true&status=open");
            return found.GetArrayLength() > 0 ? found.EnumerateArray().Select(t => t.Clone()).ToList() : null;
        });
        using var approve = await approver.PostAsJsonAsync($"/api/v1/tasks/{tasks[0].GetProperty("id").GetGuid()}/approve", new { comment = "OK" });
        Assert.True(approve.StatusCode == HttpStatusCode.OK, await approve.Content.ReadAsStringAsync());
    }

    private async Task<Env> SetUpAsync()
    {
        // Role-assigned tasks are visible to everyone with the role, so close what other tests left behind
        await using (var migrator = new NpgsqlConnection(fixture.MigrationConnectionString))
        {
            await migrator.OpenAsync();
            await using var reset = new NpgsqlCommand(
                "UPDATE core.task SET status = 'cancelled' WHERE status IN ('open', 'waiting');"
                + "UPDATE core.workflow_instance SET status = 'rejected', completed_at = now() WHERE status = 'running';",
                migrator);
            await reset.ExecuteNonQueryAsync();
        }

        var subject = $"approver-{Guid.NewGuid().ToString("N")[..8]}";
        var tenantContext = new TenantContext();
        tenantContext.Set(DemoTenantSeeder.DemoTenantId, DemoTenantSeeder.DemoUserId);
        await using (var db = fixture.CreateAppDbContext(tenantContext))
        {
            db.Users.Add(new User
            {
                Id = Guid.NewGuid(),
                TenantId = DemoTenantSeeder.DemoTenantId,
                CompanyId = DemoTenantSeeder.DemoCompanyId,
                ExternalSubject = subject,
                Email = $"{subject}@demohandel.no",
                DisplayName = "Godkjenner",
                Roles = ["finance.approver", "core.user"],
                AccessibleCompanyIds = [DemoTenantSeeder.DemoCompanyId],
                IsActive = true
            });
            await db.SaveChangesAsync();
        }

        var fake = new FakeExtractor();
        var factory = CreateFactory();
        var finance = await StartContainerAsync<FinanceApp>(factory);
        var norway = await StartContainerAsync<NorwayApp>(factory);
        var reader = await StartContainerAsync<InvoiceApp>(factory, services => services.AddSingleton<IInvoiceExtractor>(fake));
        var orchestrator = factory.Services.GetRequiredService<KubernetesAppOrchestrator>();
        orchestrator.RegisterEndpoint("finance", "1.0.0", "http://finance:50051", () => finance.GetTestServer().CreateHandler());
        orchestrator.RegisterEndpoint("norway", "1.0.0", "http://norway:50051", () => norway.GetTestServer().CreateHandler());
        orchestrator.RegisterEndpoint("invoice.claude", "1.0.0", "http://reader:50051", () => reader.GetTestServer().CreateHandler());

        var client = await LoginAsync(factory, null);
        var approver = await LoginAsync(factory, subject);

        var env = new Env(factory, finance, norway, reader, fake, client, approver, subject, fixture.MigrationConnectionString);
        foreach (var app in new[] { "core.parties", "core.dimensions", "core.organization", "core.currencies", "finance", "norway", "invoice.claude" })
        {
            using var res = await client.PostAsync($"/api/v1/apps/install-builtin/{app}", null);
            Assert.True(res.IsSuccessStatusCode, $"{app}: {await res.Content.ReadAsStringAsync()}");
        }

        // The role that approves gets the app's permission set for it (a tenant decision, made once)
        using (var role = await client.PutAsJsonAsync("/api/v1/roles/finance.approver/permission-sets", new { permissionSets = new[] { "finance.approver" } }))
        {
            Assert.Equal(HttpStatusCode.NoContent, role.StatusCode);
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

    private static async Task<HttpClient> LoginAsync(WebApplicationFactory<Program> factory, string? subject)
    {
        var client = factory.CreateClient();
        using var res = await client.PostAsJsonAsync("/api/v1/auth/dev-login", subject is null ? new { } : (object)new { subject });
        res.EnsureSuccessStatusCode();
        var token = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
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
