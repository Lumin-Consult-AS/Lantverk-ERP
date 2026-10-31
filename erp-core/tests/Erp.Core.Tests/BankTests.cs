using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using System.Xml.Schema;
using Bank.Iso20022;
using Erp.Client.Services;
using Erp.Core.Domain.Entities;
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
using Npgsql;
using Xunit;

namespace Erp.Core.Tests;

/// <summary>The bank contract in <c>finance</c> and the ISO 20022 file adapter: payment files out, statements and notifications in.</summary>
[Collection(PostgresDatabaseCollection.Name)]
public sealed class BankTests(PostgresDatabaseFixture fixture)
{
    private const string TestOidcSigningKey = "test-only-external-oidc-signing-key-at-least-32-bytes!";
    private const string TestOidcIssuer = "https://oidc.test.local/realms/erp-dev";
    private const string TestOidcAudience = "erp-web";
    private const string BankNumber = "12345678903";

    private sealed record Env(
        WebApplicationFactory<Program> Factory, WebApplication Finance, WebApplication Bank, HttpClient Client, string MigrationConnectionString, string ApproverSubject)
        : IAsyncDisposable
    {
        private static readonly string[] FinanceTables =
            ["banktransaction", "bankstatement", "payment", "paymentbatch", "bankaccount", "settlementitem", "settlement", "openitem", "posting",
             "journalentry", "voucherline", "voucher", "account"];

        public async ValueTask DisposeAsync()
        {
            using (var uninstall = await Client.DeleteAsync("/api/v1/apps/bank.iso20022?purge=true"))
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
                    + "\nDELETE FROM core.number_counter WHERE object_id LIKE 'finance.%';"
                    + "\nDELETE FROM core.workflow_definition WHERE workflow_id = 'finance.paymentbatch.approval';"
                    + "\nUPDATE core.task SET status = 'cancelled' WHERE status IN ('open', 'waiting');"
                    + "\nUPDATE core.workflow_instance SET status = 'rejected', completed_at = now() WHERE status = 'running';"
                    + $"\nDELETE FROM core.\"user\" WHERE external_subject = '{ApproverSubject}';",
                    conn);
                await cmd.ExecuteNonQueryAsync();
            }

            var archives = await Client.GetFromJsonAsync<JsonElement>("/api/v1/apps/archives");
            foreach (var archive in archives.EnumerateArray().Where(a => a.GetProperty("appId").GetString() is "finance" or "bank.iso20022"))
            {
                await Client.PostAsync($"/api/v1/apps/archives/{archive.GetProperty("id").GetGuid()}/purge", null);
            }

            foreach (var app in new[] { "core.currencies", "core.organization", "core.dimensions", "core.parties" })
            {
                await Client.DeleteAsync($"/api/v1/apps/{app}?purge=true");
            }

            Client.Dispose();
            await Bank.DisposeAsync();
            await Finance.DisposeAsync();
            await Factory.DisposeAsync();
        }
    }

    private sealed record Setup(JsonElement BankAccount, JsonElement Expense, JsonElement Payable, JsonElement SupplierA, JsonElement SupplierB, HttpClient Approver);

    // ------------------------------------------------------------------ payments out

    [Fact]
    public async Task DueSupplierItems_BecomeAPaymentBatch_ThatIsApproved_AndAPain001FileIsMade()
    {
        await using var env = await SetUpAsync();
        var c = env.Client;
        var s = await PrepareAsync(c, env);

        // Invoices from two suppliers; the last one is not due yet at the end of April
        await PostPayableAsync(c, s, s.SupplierA, "2026-03-10", 1250m, "2026-04-14");
        await PostPayableAsync(c, s, s.SupplierA, "2026-03-11", 800m, "2026-04-20");
        await PostPayableAsync(c, s, s.SupplierB, "2026-03-12", 500m, "2026-04-25");
        await PostPayableAsync(c, s, s.SupplierA, "2026-03-13", 300m, "2026-06-30");

        // A batch with a "due until" date gets a payment for each due item
        var execution = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(3).ToString("yyyy-MM-dd");
        var batch = await CreateAsync(c, "finance.paymentbatch", new
        {
            name = "Uke 17", bankAccount = Id(s.BankAccount), executionDate = execution, dueUntil = "2026-04-30"
        });
        var payments = await QueryAsync(c, "finance.payment", $"batch eq '{Id(batch)}'");
        Assert.Equal(3, payments.Count);
        Assert.Equal(1250m + 800m + 500m, payments.Sum(p => Decimal(p, "amount")));
        batch = await GetAsync(c, "finance.paymentbatch", Id(batch));
        Assert.Equal(2550m, Decimal(batch, "totalAmount"));
        Assert.Equal(3, int.Parse(Field(batch, "paymentCount")!));
        Assert.All(payments, p => Assert.Equal("NOK", Field(p, "currency")));
        Assert.Equal("98765432109", Field(payments.First(p => Field(p, "creditorName") == "Leverandør A AS"), "creditorAccount"));

        // Supplier B has no account number: the batch cannot be submitted until it is filled in
        using (var refused = await c.PostAsync($"{Url("finance.paymentbatch")}/{Id(batch)}/transitions/submit", null))
        {
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("mangler mottakers konto", await refused.Content.ReadAsStringAsync());
        }

        var b = payments.Single(p => Field(p, "creditorName") == "Leverandør B AS");
        using (var fix = await PatchAsync(c, "finance.payment", b, new { creditorAccount = "5555.55.55555", message = "Faktura 77" }))
        {
            Assert.Equal(HttpStatusCode.OK, fix.StatusCode);
        }

        var kidPayment = payments.First(p => Decimal(p, "amount") == 1250m);
        using (var kid = await PatchAsync(c, "finance.payment", kidPayment, new { paymentReference = "1234567897" }))
        {
            Assert.Equal(HttpStatusCode.OK, kid.StatusCode);
        }

        // Submitting starts the approval flow; the batch waits for the approver and nothing is sent yet
        var submitted = await TransitionAsync(c, "finance.paymentbatch", batch, "submit");
        Assert.Equal("pending", Field(submitted, "status"));
        using (var locked = await PatchAsync(c, "finance.payment", b, new { message = "Endret" }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, locked.StatusCode);
        }

        Assert.Equal("none", Field(await GetAsync(c, "finance.paymentbatch", Id(batch)), "deliveryStatus"));

        // The approver approves the task; the batch is approved and the adapter makes the file
        var tasks = await WaitForAsync(async () =>
        {
            var found = await s.Approver.GetFromJsonAsync<JsonElement>("/api/v1/tasks?mine=true&status=open");
            return found.GetArrayLength() > 0 ? found.EnumerateArray().Select(t => t.Clone()).ToList() : null;
        });
        using (var approve = await s.Approver.PostAsJsonAsync($"/api/v1/tasks/{tasks[0].GetProperty("id").GetGuid()}/approve", new { comment = "OK" }))
        {
            Assert.True(approve.StatusCode == HttpStatusCode.OK, await approve.Content.ReadAsStringAsync());
        }

        // Approved is not sent: a person releases the batch to the bank, and the adapter then acts with that person's rights
        var approvedBatch = await GetAsync(c, "finance.paymentbatch", Id(batch));
        Assert.Equal("approved", Field(approvedBatch, "status"));
        Assert.Equal("none", Field(approvedBatch, "deliveryStatus"));
        await Task.Delay(500);
        Assert.Equal("none", Field(await GetAsync(c, "finance.paymentbatch", Id(batch)), "deliveryStatus"));
        var released = await TransitionAsync(c, "finance.paymentbatch", approvedBatch, "release");
        Assert.Equal("released", Field(released, "status"));

        List<JsonElement> delivered;
        try
        {
            delivered = await WaitForAsync(async () =>
            {
                var current = await GetAsync(c, "finance.paymentbatch", Id(batch));
                return Field(current, "deliveryStatus") != "none" ? [current] : null;
            });
        }
        catch (TimeoutException)
        {
            var events = await c.GetStringAsync("/api/v1/events?name=finance.paymentbatch.approved@1&top=5");
            throw new Exception((await GetAsync(c, "finance.paymentbatch", Id(batch))).ToString() + " EVENTS " + events);
        }
        var done = delivered[0];
        Assert.True(Field(done, "deliveryStatus") == "sent", Field(done, "message"));
        Assert.Equal("released", Field(done, "status"));

        // The file is valid pain.001.001.03 and carries the batch
        var bytes = await c.GetByteArrayAsync($"/api/v1/files/{Field(done, "file")}/content");
        Assert.Empty(Pain001.Validate(bytes));
        XNamespace ns = Pain001.Namespace;
        var xml = XDocument.Load(new MemoryStream(bytes)).Root!;
        var group = xml.Descendants(ns + "GrpHdr").Single();
        Assert.Equal("3", group.Element(ns + "NbOfTxs")!.Value);
        Assert.Equal("2550.00", group.Element(ns + "CtrlSum")!.Value);
        var info = xml.Descendants(ns + "PmtInf").Single();
        Assert.Equal(execution, info.Element(ns + "ReqdExctnDt")!.Value);
        Assert.Equal("Demohandel AS", info.Element(ns + "Dbtr")!.Element(ns + "Nm")!.Value);
        Assert.Equal(BankNumber, info.Element(ns + "DbtrAcct")!.Descendants(ns + "Id").Last().Value);
        var transfers = info.Elements(ns + "CdtTrfTxInf").ToList();
        Assert.Equal(3, transfers.Count);
        var first = transfers.Single(t => t.Descendants(ns + "InstdAmt").Single().Value == "1250.00");
        Assert.Equal("NOK", first.Descendants(ns + "InstdAmt").Single().Attribute("Ccy")!.Value);
        Assert.Equal("98765432109", first.Element(ns + "CdtrAcct")!.Descendants(ns + "Id").Last().Value);
        Assert.Equal("1234567897", first.Descendants(ns + "Ref").Single().Value);
        var second = transfers.Single(t => t.Descendants(ns + "InstdAmt").Single().Value == "500.00");
        Assert.Equal("55555555555", second.Element(ns + "CdtrAcct")!.Descendants(ns + "Id").Last().Value);
        Assert.Equal("Faktura 77", second.Descendants(ns + "Ustrd").Single().Value);

        // The items are in a batch: a new batch gets only what is left
        var next = await CreateAsync(c, "finance.paymentbatch", new
        {
            name = "Uke 27", bankAccount = Id(s.BankAccount), executionDate = execution, dueUntil = "2026-07-15"
        });
        var left = await QueryAsync(c, "finance.payment", $"batch eq '{Id(next)}'");
        Assert.Equal(300m, Decimal(Assert.Single(left), "amount"));
    }

    [Fact]
    public async Task ABatchThatIsNotComplete_CannotBeSubmitted()
    {
        await using var env = await SetUpAsync();
        var c = env.Client;
        var s = await PrepareAsync(c, env);

        // No payments
        var empty = await CreateAsync(c, "finance.paymentbatch", new
        {
            name = "Tom", bankAccount = Id(s.BankAccount), executionDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(2).ToString("yyyy-MM-dd")
        });
        using (var refused = await c.PostAsync($"{Url("finance.paymentbatch")}/{Id(empty)}/transitions/submit", null))
        {
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("ingen betalinger", await refused.Content.ReadAsStringAsync());
        }

        // A payment date that has passed, and a payment in another currency than the account
        var past = await CreateAsync(c, "finance.paymentbatch", new { name = "Gammel", bankAccount = Id(s.BankAccount), executionDate = "2020-01-02" });
        await CreateAsync(c, "finance.payment", new
        {
            batch = Id(past), party = Id(s.SupplierA), creditorName = "Leverandør A AS", creditorAccount = "98765432109", amount = 10m, currency = "EUR"
        });
        using (var refusedPast = await c.PostAsync($"{Url("finance.paymentbatch")}/{Id(past)}/transitions/submit", null))
        {
            Assert.Equal(HttpStatusCode.BadRequest, refusedPast.StatusCode);
            Assert.Contains("passert", await refusedPast.Content.ReadAsStringAsync());
        }

        var future = await CreateAsync(c, "finance.paymentbatch", new
        {
            name = "Valuta", bankAccount = Id(s.BankAccount), executionDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(2).ToString("yyyy-MM-dd")
        });
        var euro = await CreateAsync(c, "finance.payment", new
        {
            batch = Id(future), party = Id(s.SupplierA), creditorName = "Leverandør A AS", creditorAccount = "98765432109", amount = 10m, currency = "EUR"
        });
        using (var refusedCurrency = await c.PostAsync($"{Url("finance.paymentbatch")}/{Id(future)}/transitions/submit", null))
        {
            Assert.Equal(HttpStatusCode.BadRequest, refusedCurrency.StatusCode);
            Assert.Contains("valuta", await refusedCurrency.Content.ReadAsStringAsync());
        }

        // Removing a payment brings the batch's sum back in step
        using (var remove = new HttpRequestMessage(HttpMethod.Delete, $"{Url("finance.payment")}/{Id(euro)}"))
        {
            var current = await c.GetFromJsonAsync<JsonElement>($"{Url("finance.payment")}/{Id(euro)}");
            remove.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{current.GetProperty("rowVersion").GetInt64()}\""));
            using var res = await c.SendAsync(remove);
            Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        }

        var emptied = await GetAsync(c, "finance.paymentbatch", Id(future));
        Assert.Equal(0m, Decimal(emptied, "totalAmount"));
    }

    // ------------------------------------------------------------------ statements in

    [Fact]
    public async Task AStatementAndANotification_AreReadIn_OnceOnly_AndMustBelongToTheChosenAccount()
    {
        await using var env = await SetUpAsync();
        var c = env.Client;
        var s = await PrepareAsync(c, env);

        var statement = CamtStatement(BankNumber);
        AssertValid(statement, "camt.053.001.02.xsd");
        var file = await UploadAsync(c, "utskrift.xml", statement);

        var import = await ImportAsync(c, file, Id(s.BankAccount));
        Assert.True(Field(import, "status") == "done", Field(import, "message"));
        Assert.Equal(2, int.Parse(Field(import, "transactionCount")!));
        Assert.Equal(1, int.Parse(Field(import, "statementCount")!));

        var stmt = Assert.Single(await QueryAsync(c, "finance.bankstatement", "externalId ne ''"));
        Assert.Equal(10000m, Decimal(stmt, "openingBalance"));
        Assert.Equal(10750m, Decimal(stmt, "closingBalance"));
        Assert.Equal("2026-04-01", Field(stmt, "fromDate"));
        var transactions = await QueryAsync(c, "finance.banktransaction", "externalId ne ''");
        Assert.Equal(2, transactions.Count);
        var incoming = transactions.Single(t => Decimal(t, "amount") > 0);
        Assert.Equal(1250m, Decimal(incoming, "amount"));
        Assert.Equal("Kunde 1 AS", Field(incoming, "counterpartyName"));
        Assert.Equal("1234567897", Field(incoming, "paymentReference"));
        Assert.Equal("2026-04-14", Field(incoming, "bookingDate"));
        var outgoing = transactions.Single(t => Decimal(t, "amount") < 0);
        Assert.Equal(-500m, Decimal(outgoing, "amount"));
        Assert.Equal("Faktura 77", Field(outgoing, "message"));
        Assert.Equal("55555555555", Field(outgoing, "counterpartyAccount"));

        // Reading the same file again adds nothing
        var again = await ImportAsync(c, file, Id(s.BankAccount));
        Assert.Equal("done", Field(again, "status"));
        Assert.Equal(0, int.Parse(Field(again, "transactionCount")!));
        Assert.Equal(2, int.Parse(Field(again, "duplicateCount")!));
        Assert.Equal(2, (await QueryAsync(c, "finance.banktransaction", "externalId ne ''")).Count);

        // A notification of an incoming payment adds a transaction without a statement
        var notification = CamtNotification(BankNumber);
        AssertValid(notification, "camt.054.001.02.xsd");
        var notified = await ImportAsync(c, await UploadAsync(c, "melding.xml", notification), Id(s.BankAccount));
        Assert.True(Field(notified, "status") == "done", Field(notified, "message"));
        Assert.Equal(1, int.Parse(Field(notified, "transactionCount")!));
        Assert.Single(await QueryAsync(c, "finance.banktransaction", "paymentReference eq '9876543210'"));

        // A file for another account is refused, and so is something that is not a bank file
        var other = await ImportAsync(c, await UploadAsync(c, "annen.xml", CamtStatement("11111111119")), Id(s.BankAccount));
        Assert.Equal("failed", Field(other, "status"));
        Assert.Contains("gjelder konto 11111111119", Field(other, "message"));
        var junk = await ImportAsync(c, await UploadAsync(c, "tull.xml", Encoding.UTF8.GetBytes("<a><b/></a>")), Id(s.BankAccount));
        Assert.Equal("failed", Field(junk, "status"));
        Assert.Contains("hverken", Field(junk, "message"));
    }

    private static string CamtNs(string version) => $"urn:iso:std:iso:20022:tech:xsd:camt.{version}.001.02";

    private static byte[] CamtStatement(string account) => Encoding.UTF8.GetBytes($"""
        <?xml version="1.0" encoding="UTF-8"?>
        <Document xmlns="{CamtNs("053")}">
          <BkToCstmrStmt>
            <GrpHdr><MsgId>MSG-1</MsgId><CreDtTm>2026-04-30T23:00:00</CreDtTm></GrpHdr>
            <Stmt>
              <Id>STMT-2026-04</Id>
              <CreDtTm>2026-04-30T23:00:00</CreDtTm>
              <FrToDt><FrDtTm>2026-04-01T00:00:00</FrDtTm><ToDtTm>2026-04-30T23:59:59</ToDtTm></FrToDt>
              <Acct><Id><Othr><Id>{account}</Id></Othr></Id><Ccy>NOK</Ccy></Acct>
              <Bal><Tp><CdOrPrtry><Cd>OPBD</Cd></CdOrPrtry></Tp><Amt Ccy="NOK">10000.00</Amt><CdtDbtInd>CRDT</CdtDbtInd><Dt><Dt>2026-04-01</Dt></Dt></Bal>
              <Bal><Tp><CdOrPrtry><Cd>CLBD</Cd></CdOrPrtry></Tp><Amt Ccy="NOK">10750.00</Amt><CdtDbtInd>CRDT</CdtDbtInd><Dt><Dt>2026-04-30</Dt></Dt></Bal>
              <Ntry>
                <Amt Ccy="NOK">1250.00</Amt><CdtDbtInd>CRDT</CdtDbtInd><Sts>BOOK</Sts>
                <BookgDt><Dt>2026-04-14</Dt></BookgDt><ValDt><Dt>2026-04-14</Dt></ValDt>
                <AcctSvcrRef>REF1</AcctSvcrRef>
                <BkTxCd/>
                <NtryDtls><TxDtls>
                  <Refs><EndToEndId>E2E-1</EndToEndId></Refs>
                  <RltdPties><Dbtr><Nm>Kunde 1 AS</Nm></Dbtr><DbtrAcct><Id><Othr><Id>22222222226</Id></Othr></Id></DbtrAcct></RltdPties>
                  <RmtInf><Strd><CdtrRefInf><Tp><CdOrPrtry><Prtry>KID</Prtry></CdOrPrtry></Tp><Ref>1234567897</Ref></CdtrRefInf></Strd></RmtInf>
                </TxDtls></NtryDtls>
              </Ntry>
              <Ntry>
                <Amt Ccy="NOK">500.00</Amt><CdtDbtInd>DBIT</CdtDbtInd><Sts>BOOK</Sts>
                <BookgDt><Dt>2026-04-25</Dt></BookgDt><ValDt><Dt>2026-04-25</Dt></ValDt>
                <AcctSvcrRef>REF2</AcctSvcrRef>
                <BkTxCd/>
                <NtryDtls><TxDtls>
                  <RltdPties><Cdtr><Nm>Leverandør B AS</Nm></Cdtr><CdtrAcct><Id><Othr><Id>55555555555</Id></Othr></Id></CdtrAcct></RltdPties>
                  <RmtInf><Ustrd>Faktura 77</Ustrd></RmtInf>
                </TxDtls></NtryDtls>
              </Ntry>
            </Stmt>
          </BkToCstmrStmt>
        </Document>
        """);

    private static byte[] CamtNotification(string account) => Encoding.UTF8.GetBytes($"""
        <?xml version="1.0" encoding="UTF-8"?>
        <Document xmlns="{CamtNs("054")}">
          <BkToCstmrDbtCdtNtfctn>
            <GrpHdr><MsgId>MSG-2</MsgId><CreDtTm>2026-05-02T10:00:00</CreDtTm></GrpHdr>
            <Ntfctn>
              <Id>NTF-1</Id>
              <CreDtTm>2026-05-02T10:00:00</CreDtTm>
              <Acct><Id><Othr><Id>{account}</Id></Othr></Id></Acct>
              <Ntry>
                <Amt Ccy="NOK">980.00</Amt><CdtDbtInd>CRDT</CdtDbtInd><Sts>BOOK</Sts>
                <BookgDt><Dt>2026-05-02</Dt></BookgDt>
                <AcctSvcrRef>REF3</AcctSvcrRef>
                <BkTxCd/>
                <NtryDtls><TxDtls>
                  <RltdPties><Dbtr><Nm>Kunde 2 AS</Nm></Dbtr></RltdPties>
                  <RmtInf><Strd><CdtrRefInf><Tp><CdOrPrtry><Prtry>KID</Prtry></CdOrPrtry></Tp><Ref>9876543210</Ref></CdtrRefInf></Strd></RmtInf>
                </TxDtls></NtryDtls>
              </Ntry>
            </Ntfctn>
          </BkToCstmrDbtCdtNtfctn>
        </Document>
        """);

    private static void AssertValid(byte[] xml, string schemaFile)
    {
        var schemas = new XmlSchemaSet();
        var path = Path.Combine(System.AppContext.BaseDirectory, "TestData", "iso", schemaFile);
        schemas.Add(null, path);
        var errors = new List<string>();
        var settings = new System.Xml.XmlReaderSettings { ValidationType = System.Xml.ValidationType.Schema, Schemas = schemas };
        settings.ValidationEventHandler += (_, e) => errors.Add(e.Message);
        using var reader = System.Xml.XmlReader.Create(new MemoryStream(xml), settings);
        while (reader.Read())
        {
        }

        Assert.Empty(errors);
    }

    // ------------------------------------------------------------------ helpers

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

    private static async Task<JsonElement> PostPayableAsync(HttpClient c, Setup s, JsonElement supplier, string date, decimal amount, string due)
    {
        var voucher = await CreateAsync(c, "finance.voucher", new { voucherDate = date, description = $"Faktura {amount}" });
        await CreateAsync(c, "finance.voucherline", new { voucher = Id(voucher), account = Id(s.Expense), debit = amount });
        await CreateAsync(c, "finance.voucherline", new
        {
            voucher = Id(voucher), account = Id(s.Payable), credit = amount, party = Id(supplier), dueDate = due
        });
        return await TransitionAsync(c, "finance.voucher", voucher, "post");
    }

    private async Task<Setup> PrepareAsync(HttpClient c, Env env)
    {
        var bankLedger = await CreateAsync(c, "finance.account", new { number = "1920", name = "Bank", accountType = "asset" });
        var expense = await CreateAsync(c, "finance.account", new { number = "6300", name = "Leie", accountType = "expense" });
        var payable = await CreateAsync(c, "finance.account", new { number = "2400", name = "Leverandørgjeld", accountType = "liability", controlType = "payable" });
        var bank = await CreateAsync(c, "finance.bankaccount", new
        {
            name = "Driftskonto", accountNumber = BankNumber, holderName = "Demohandel AS", holderId = "999888777",
            ledgerAccount = Id(bankLedger), currency = "NOK"
        });
        var a = await CreateAsync(c, "core.party", new Dictionary<string, object?>
        {
            ["partyNumber"] = "L-A", ["name"] = "Leverandør A AS", ["partyType"] = "organization", ["role"] = "supplier",
            ["organizationNumber"] = "955777888", ["finance.bankAccount"] = "98765432109"
        });
        var b = await CreateAsync(c, "core.party", new
        {
            partyNumber = "L-B", name = "Leverandør B AS", partyType = "organization", role = "supplier", organizationNumber = "944555666"
        });

        // The role that approves payments gets the app's permission set for it (a tenant decision, made once)
        using (var role = await c.PutAsJsonAsync("/api/v1/roles/finance.approver/permission-sets", new { permissionSets = new[] { "finance.approver" } }))
        {
            Assert.Equal(HttpStatusCode.NoContent, role.StatusCode);
        }

        var approver = env.Factory.CreateClient();
        using (var login = await approver.PostAsJsonAsync("/api/v1/auth/dev-login", new { subject = env.ApproverSubject }))
        {
            login.EnsureSuccessStatusCode();
            var token = JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.GetProperty("accessToken").GetString();
            approver.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return new Setup(bank, expense, payable, a, b, approver);
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

        var factory = CreateFactory();
        var finance = await StartContainerAsync<FinanceApp>(factory);
        var bank = await StartContainerAsync<BankApp>(factory);
        var orchestrator = factory.Services.GetRequiredService<KubernetesAppOrchestrator>();
        orchestrator.RegisterEndpoint("finance", "1.0.0", "http://finance:50051", () => finance.GetTestServer().CreateHandler());
        orchestrator.RegisterEndpoint("bank.iso20022", "1.0.0", "http://bank:50051", () => bank.GetTestServer().CreateHandler());

        var client = factory.CreateClient();
        using (var res = await client.PostAsJsonAsync("/api/v1/auth/dev-login", new { }))
        {
            res.EnsureSuccessStatusCode();
            var token = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("accessToken").GetString();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var env = new Env(factory, finance, bank, client, fixture.MigrationConnectionString, subject);
        foreach (var app in new[] { "core.parties", "core.dimensions", "core.organization", "core.currencies", "finance", "bank.iso20022" })
        {
            using var res = await client.PostAsync($"/api/v1/apps/install-builtin/{app}", null);
            Assert.True(res.IsSuccessStatusCode, $"{app}: {await res.Content.ReadAsStringAsync()}");
        }

        var year = await CreateAsync(client, "core.fiscalyear", new { year = 2026, startDate = "2026-01-01", endDate = "2026-12-31" });
        await WaitForAsync(async () =>
        {
            var found = await QueryAsync(client, "core.fiscalperiod", $"fiscalYear eq '{Id(year)}'", 50);
            return found.Count == 12 ? found : null;
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

    private static async Task<HttpResponseMessage> PatchAsync(HttpClient c, string objectId, JsonElement record, object body)
    {
        var current = await c.GetFromJsonAsync<JsonElement>($"{Url(objectId)}/{Id(record)}");
        using var req = new HttpRequestMessage(HttpMethod.Patch, $"{Url(objectId)}/{Id(record)}");
        req.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{current.GetProperty("rowVersion").GetInt64()}\""));
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
