using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Erp.Client.Services;
using Erp.Core.Domain.Entities;
using Erp.Core.Domain.Tenancy;
using Erp.Core.Infrastructure.Apps;
using Erp.Core.Infrastructure.Documents;
using Erp.Core.Infrastructure.Notifications;
using Erp.Core.Infrastructure.Persistence;
using Erp.Core.Infrastructure.Storage;
using Erp.Sdk.Contracts.V1;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Xunit;

namespace Erp.Core.Tests;

[Collection(PostgresDatabaseCollection.Name)]
public sealed class DocumentTests(PostgresDatabaseFixture fixture) : IAsyncLifetime
{
    private const string TestOidcSigningKey = "test-only-external-oidc-signing-key-at-least-32-bytes!";
    private const string TestOidcIssuer = "https://oidc.test.local/realms/erp-dev";
    private const string TestOidcAudience = "erp-web";
    private const string WebhookToken = "test-brevo-webhook-token";
    private const string Eicar = "X5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-TEST-FILE!$H+H*";

    private readonly List<string> _createdSubjects = [];

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>Other tests expect the demo tenant to have exactly its seeded user, so remove the users created here.</summary>
    public async Task DisposeAsync()
    {
        if (_createdSubjects.Count == 0)
        {
            return;
        }

        await using var migrator = new NpgsqlConnection(fixture.MigrationConnectionString);
        await migrator.OpenAsync();
        await using var cmd = new NpgsqlCommand("""DELETE FROM core."user" WHERE external_subject = ANY(@subjects);""", migrator);
        cmd.Parameters.AddWithValue("subjects", _createdSubjects.ToArray());
        await cmd.ExecuteNonQueryAsync();
    }

    // ------------------------------------------------------------------ fakes

    private sealed class FakeScanner : IVirusScanner
    {
        public bool Unavailable { get; set; }

        public Task<ScanResult> ScanAsync(byte[] content, CancellationToken cancellationToken)
        {
            if (Unavailable)
            {
                return Task.FromResult(new ScanResult(ScanOutcome.Unavailable, Error: "scanner down"));
            }

            return Task.FromResult(
                Encoding.UTF8.GetString(content).Contains("EICAR-TEST-FILE", StringComparison.Ordinal)
                    ? new ScanResult(ScanOutcome.Infected, "Eicar-Test-Signature")
                    : new ScanResult(ScanOutcome.Clean));
        }
    }

    private sealed class FakeBrevo : IBrevoAttachmentClient
    {
        public ConcurrentDictionary<string, byte[]> Attachments { get; } = new();

        public Task<byte[]> DownloadAsync(string downloadToken, CancellationToken cancellationToken) =>
            Task.FromResult(Attachments[downloadToken]);
    }

    private sealed class FakePdfRenderer : IPdfRenderer
    {
        public Task<byte[]> RenderAsync(string html, CancellationToken cancellationToken) =>
            Task.FromResult(Encoding.UTF8.GetBytes("%PDF-fake\n" + html));
    }

    private sealed class CapturingEmailSender : IEmailSender
    {
        public ConcurrentQueue<EmailMessage> Sent { get; } = new();

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            Sent.Enqueue(message);
            return Task.CompletedTask;
        }
    }

    private sealed class Env(
        WebApplicationFactory<Program> factory,
        FakeScanner scanner,
        FakeBrevo brevo,
        CapturingEmailSender email) : IAsyncDisposable
    {
        public WebApplicationFactory<Program> Factory { get; } = factory;
        public FakeScanner Scanner { get; } = scanner;
        public FakeBrevo Brevo { get; } = brevo;
        public CapturingEmailSender Email { get; } = email;

        public ValueTask DisposeAsync() => Factory.DisposeAsync();
    }

    private sealed class Session(HttpClient http, ErpApiClient api)
    {
        public HttpClient Http { get; } = http;
        public ErpApiClient Api { get; } = api;
    }

    // ------------------------------------------------------------------ the demo: e-mail with a PDF

    [Fact]
    public async Task EmailWithPdf_LandsInTheInbox_IsAnnounced_AndCanBeAttachedToAParty_UnderRetentionLock()
    {
        await using var env = CreateEnv();
        var admin = await LoginAsync(env.Factory, null);
        Assert.True(await admin.Api.InstallBuiltInAppAsync("core.parties"));

        try
        {
            var (inboxId, address) = await CreateInboxAsync(admin, "email", "Bilag");
            var party = await admin.Api.CreateRecordAsync("core.party", Party("P-DOC-1"));

            var pdf = Encoding.UTF8.GetBytes("%PDF-1.4 faktura 1001 " + Guid.NewGuid());
            env.Brevo.Attachments["tok-1"] = pdf;
            var subject = $"Faktura 1001 {Guid.NewGuid():N}";
            var before = await InboxAttentionCountAsync(admin);

            // 1. Brevo calls the webhook; the unknown recipient and the wrong token are handled
            using var anonymous = env.Factory.CreateClient();
            using var wrongToken = await anonymous.PostAsJsonAsync("/api/v1/inbound/brevo?token=nope", new { });
            Assert.Equal(HttpStatusCode.Unauthorized, wrongToken.StatusCode);

            using var notJson = await anonymous.PostAsync(
                $"/api/v1/inbound/brevo?token={WebhookToken}", new ByteArrayContent([0xFF, 0xFE, 0x7B, 0x80]));
            Assert.Equal(HttpStatusCode.BadRequest, notJson.StatusCode);

            using var noInbox = await anonymous.PostAsync(
                $"/api/v1/inbound/brevo?token={WebhookToken}", Json(BrevoPayload("ingen@inbox.test.local", "<m0@x>", subject)));
            Assert.Equal(HttpStatusCode.OK, noInbox.StatusCode);
            Assert.Equal(0, JsonDocument.Parse(await noInbox.Content.ReadAsStringAsync()).RootElement.GetProperty("items").GetInt32());

            var payload = BrevoPayload(address, "<m1@x>", subject, ("faktura.pdf", "tok-1", "application/pdf"));
            using var delivered = await anonymous.PostAsync($"/api/v1/inbound/brevo?token={WebhookToken}", Json(payload));
            Assert.Equal(HttpStatusCode.OK, delivered.StatusCode);
            Assert.Equal(1, JsonDocument.Parse(await delivered.Content.ReadAsStringAsync()).RootElement.GetProperty("items").GetInt32());

            // 2. A redelivery of the same message (Brevo retries) does not create a second item
            using var redelivered = await anonymous.PostAsync($"/api/v1/inbound/brevo?token={WebhookToken}", Json(payload));
            Assert.Equal(HttpStatusCode.OK, redelivered.StatusCode);
            var items = await ItemsAsync(admin, subject);
            var item = Assert.Single(items);
            Assert.Equal("new", item.GetProperty("status").GetString());
            Assert.Equal("kunde@leverandor.no", item.GetProperty("sender").GetString());
            var itemId = item.GetProperty("id").GetGuid();
            var fileId = Assert.Single(item.GetProperty("fileIds").EnumerateArray()).GetGuid();

            // 3. The file is in the object store and the start page shows the new document
            Assert.Equal(pdf, await DownloadAsync(admin, fileId));
            Assert.Equal(before + 1, await InboxAttentionCountAsync(admin));

            // 4. core.inbox.received@1 was published in the log
            using var events = JsonDocument.Parse(await admin.Http.GetStringAsync("/api/v1/events?name=core.inbox.received@1&top=200"));
            Assert.Single(events.RootElement.EnumerateArray(), e => e.GetProperty("aggregateId").GetString() == itemId.ToString("D"));

            // 5. The item is attached to a party as a voucher; the item is processed
            using var attach = await admin.Http.PostAsJsonAsync(
                $"/api/v1/inbox/items/{itemId}/attach", new { objectId = "core.party", recordId = party.Id, category = "bilag" });
            Assert.Equal(HttpStatusCode.OK, attach.StatusCode);
            var processed = JsonDocument.Parse(await attach.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal("processed", processed.GetProperty("status").GetString());
            Assert.Equal(party.Id, processed.GetProperty("resultRecordId").GetGuid());
            Assert.Equal(before, await InboxAttentionCountAsync(admin));

            using var attachments = JsonDocument.Parse(
                await admin.Http.GetStringAsync($"/api/v1/attachments?objectId=core.party&recordId={party.Id}"));
            var attachment = Assert.Single(attachments.RootElement.EnumerateArray());
            Assert.Equal("bilag", attachment.GetProperty("category").GetString());

            // 6. Vouchers are kept 7 years in Norway (configured override of the 5-year default) and locked in the store
            var retainUntil = attachment.GetProperty("retainUntil").GetDateTimeOffset();
            Assert.InRange(retainUntil, DateTimeOffset.UtcNow.AddYears(7).AddDays(-1), DateTimeOffset.UtcNow.AddYears(7).AddDays(1));

            var key = await StorageKeyAsync(fileId);
            var storage = env.Factory.Services.GetRequiredService<IObjectStorage>();
            var locked = await storage.GetLockedUntilAsync(key);
            Assert.NotNull(locked);
            Assert.InRange(locked.Value, retainUntil.AddMinutes(-1), retainUntil.AddMinutes(1));

            // 7. Nobody can remove it during retention: not through the API, not directly in the object store
            using var detach = await admin.Http.DeleteAsync($"/api/v1/attachments/{attachment.GetProperty("id").GetGuid()}");
            Assert.Equal(HttpStatusCode.Conflict, detach.StatusCode);
            using var deleteFile = await admin.Http.DeleteAsync($"/api/v1/files/{fileId}");
            Assert.Equal(HttpStatusCode.Conflict, deleteFile.StatusCode);
            await Assert.ThrowsAsync<ObjectLockedException>(() => storage.DeleteAsync(key));
            Assert.Equal(pdf, await storage.GetAsync(key));

            // 8. The documents belong to the tenant: another tenant sees none of them
            await using var conn = new NpgsqlConnection(fixture.ApplicationConnectionString);
            await conn.OpenAsync();
            await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, false);", conn))
            {
                set.Parameters.AddWithValue("t", Guid.NewGuid().ToString("D"));
                await set.ExecuteNonQueryAsync();
            }

            await using var count = new NpgsqlCommand(
                "SELECT (SELECT count(*) FROM core.file) + (SELECT count(*) FROM core.inboxitem) + (SELECT count(*) FROM core.attachment);", conn);
            Assert.Equal(0L, await count.ExecuteScalarAsync());
        }
        finally
        {
            await admin.Api.UninstallAppAsync("core.parties");
        }
    }

    [Fact]
    public async Task Files_AreDeduplicatedPerCompany_AndFilesWithoutRetentionCanBeRemoved()
    {
        await using var env = CreateEnv();
        var admin = await LoginAsync(env.Factory, null);
        Assert.True(await admin.Api.InstallBuiltInAppAsync("core.parties"));

        try
        {
            var party = await admin.Api.CreateRecordAsync("core.party", Party("P-DOC-2"));
            var content = Encoding.UTF8.GetBytes("avtale " + Guid.NewGuid());

            using var first = await UploadAsync(admin, "avtale.pdf", content);
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
            var firstFile = JsonDocument.Parse(await first.Content.ReadAsStringAsync()).RootElement;
            var fileId = firstFile.GetProperty("file").GetProperty("id").GetGuid();
            Assert.False(firstFile.GetProperty("duplicate").GetBoolean());
            Assert.Equal(64, firstFile.GetProperty("file").GetProperty("sha256").GetString()!.Length);

            // The same content again: not stored twice, and the user is told
            using var second = await UploadAsync(admin, "kopi.pdf", content);
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            var secondFile = JsonDocument.Parse(await second.Content.ReadAsStringAsync()).RootElement;
            Assert.True(secondFile.GetProperty("duplicate").GetBoolean());
            Assert.Equal(fileId, secondFile.GetProperty("file").GetProperty("id").GetGuid());

            // A category without retention can be attached and removed again, and then the file can be deleted
            using var attach = await admin.Http.PostAsJsonAsync(
                "/api/v1/attachments", new { fileId, objectId = "core.party", recordId = party.Id, category = "avtale" });
            Assert.Equal(HttpStatusCode.Created, attach.StatusCode);
            var attachment = JsonDocument.Parse(await attach.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal(JsonValueKind.Null, attachment.GetProperty("retainUntil").ValueKind);

            using var deleteWhileAttached = await admin.Http.DeleteAsync($"/api/v1/files/{fileId}");
            Assert.Equal(HttpStatusCode.Conflict, deleteWhileAttached.StatusCode);

            using var detach = await admin.Http.DeleteAsync($"/api/v1/attachments/{attachment.GetProperty("id").GetGuid()}");
            Assert.Equal(HttpStatusCode.NoContent, detach.StatusCode);

            var key = await StorageKeyAsync(fileId);
            using var delete = await admin.Http.DeleteAsync($"/api/v1/files/{fileId}");
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
            Assert.Null(await env.Factory.Services.GetRequiredService<IObjectStorage>().GetAsync(key));

            // A file cannot be attached to a record that does not exist
            using var other = await UploadAsync(admin, "x.pdf", Encoding.UTF8.GetBytes("x " + Guid.NewGuid()));
            var otherId = JsonDocument.Parse(await other.Content.ReadAsStringAsync()).RootElement.GetProperty("file").GetProperty("id").GetGuid();
            using var missing = await admin.Http.PostAsJsonAsync(
                "/api/v1/attachments", new { fileId = otherId, objectId = "core.party", recordId = Guid.NewGuid(), category = "avtale" });
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        finally
        {
            await admin.Api.UninstallAppAsync("core.parties");
        }
    }

    [Fact]
    public async Task AnInstalledApp_CanSetTheRetentionOfACategoryForACountry()
    {
        await using var env = CreateEnv();
        var admin = await LoginAsync(env.Factory, null);
        Assert.True(await admin.Api.InstallBuiltInAppAsync("core.parties"));

        try
        {
            var party = await admin.Api.CreateRecordAsync("core.party", Party("P-DOC-3"));
            async Task<JsonElement> AttachAsync(string category)
            {
                using var upload = await UploadAsync(admin, "k.pdf", Encoding.UTF8.GetBytes("kontrakt " + Guid.NewGuid()));
                var fileId = JsonDocument.Parse(await upload.Content.ReadAsStringAsync()).RootElement.GetProperty("file").GetProperty("id").GetGuid();
                using var attach = await admin.Http.PostAsJsonAsync(
                    "/api/v1/attachments", new { fileId, objectId = "core.party", recordId = party.Id, category });
                Assert.Equal(HttpStatusCode.Created, attach.StatusCode);
                return JsonDocument.Parse(await attach.Content.ReadAsStringAsync()).RootElement.Clone();
            }

            async Task<HttpResponseMessage> InstallAsync(string yaml)
            {
                var package = new ByteArrayContent(AppPackageFormat.CreateSignedZipFromFiles(new Dictionary<string, string> { ["app.yaml"] = yaml }));
                package.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
                return await admin.Http.PostAsync("/api/v1/apps/install", package);
            }

            // No rule yet: the category is not kept
            Assert.Equal(JsonValueKind.Null, (await AttachAsync("kontrakt")).GetProperty("retainUntil").ValueKind);

            // An installed app brings the rule for Norway, and the document service applies it
            using (var install = await InstallAsync(
                       "id: demo.retention\nversion: 1.0.0\nname: Oppbevaring\nretention:\n  - country: NO\n    category: kontrakt\n    years: 9\n"))
            {
                Assert.True(install.IsSuccessStatusCode, await install.Content.ReadAsStringAsync());
            }

            var retainUntil = (await AttachAsync("kontrakt")).GetProperty("retainUntil").GetDateTimeOffset();
            Assert.InRange(retainUntil, DateTimeOffset.UtcNow.AddYears(9).AddDays(-1), DateTimeOffset.UtcNow.AddYears(9).AddDays(1));
            Assert.Equal(JsonValueKind.Null, (await AttachAsync("avtale")).GetProperty("retainUntil").ValueKind);

            // An invalid rule is refused when installing
            using var refused = await InstallAsync(
                "id: demo.retention2\nversion: 1.0.0\nname: Feil\nretention:\n  - country: Norge\n    category: kontrakt\n    years: 0\n");
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }
        finally
        {
            await admin.Api.UninstallAppAsync("demo.retention");
            await admin.Api.UninstallAppAsync("core.parties");
        }
    }

    [Fact]
    public async Task InfectedFiles_AreNeverStoredOrServed_AndRejectTheEmailThatCarriedThem()
    {
        await using var env = CreateEnv();
        var admin = await LoginAsync(env.Factory, null);

        var (_, address) = await CreateInboxAsync(admin, "email", "Virus");
        using var upload = await UploadAsync(admin, "ond.txt", Encoding.UTF8.GetBytes(Eicar + Guid.NewGuid()));
        var file = JsonDocument.Parse(await upload.Content.ReadAsStringAsync()).RootElement.GetProperty("file");
        Assert.Equal("infected", file.GetProperty("scanStatus").GetString());
        Assert.Equal("Eicar-Test-Signature", file.GetProperty("scanSignature").GetString());

        using var download = await admin.Http.GetAsync($"/api/v1/files/{file.GetProperty("id").GetGuid()}/content");
        Assert.Equal(HttpStatusCode.Locked, download.StatusCode);
        Assert.Null(await env.Factory.Services.GetRequiredService<IObjectStorage>().GetAsync(await StorageKeyAsync(file.GetProperty("id").GetGuid())));

        env.Brevo.Attachments["tok-evil"] = Encoding.UTF8.GetBytes(Eicar + Guid.NewGuid());
        env.Brevo.Attachments["tok-good"] = Encoding.UTF8.GetBytes("ren fil " + Guid.NewGuid());
        var subject = $"Mistenkelig {Guid.NewGuid():N}";
        using var anonymous = env.Factory.CreateClient();
        using var delivered = await anonymous.PostAsync(
            $"/api/v1/inbound/brevo?token={WebhookToken}",
            Json(BrevoPayload(address, "<virus@x>", subject, ("a.pdf", "tok-good", "application/pdf"), ("b.exe", "tok-evil", "application/octet-stream"))));
        Assert.Equal(HttpStatusCode.OK, delivered.StatusCode);

        var item = Assert.Single(await ItemsAsync(admin, subject));
        Assert.Equal("rejected", item.GetProperty("status").GetString());
        Assert.Contains("Eicar-Test-Signature", item.GetProperty("rejectReason").GetString());

        using var events = JsonDocument.Parse(await admin.Http.GetStringAsync("/api/v1/events?name=core.inbox.received@1&top=200"));
        Assert.DoesNotContain(events.RootElement.EnumerateArray(), e => e.GetProperty("aggregateId").GetString() == item.GetProperty("id").GetGuid().ToString("D"));
    }

    [Fact]
    public async Task FilesStayUnavailable_WhileTheScannerIsDown_AndAreReleasedWhenItIsBack()
    {
        await using var env = CreateEnv();
        env.Scanner.Unavailable = true;
        var admin = await LoginAsync(env.Factory, null);
        var (_, address) = await CreateInboxAsync(admin, "email", "Skanner");

        using var upload = await UploadAsync(admin, "venter.pdf", Encoding.UTF8.GetBytes("venter " + Guid.NewGuid()));
        var fileId = JsonDocument.Parse(await upload.Content.ReadAsStringAsync()).RootElement.GetProperty("file").GetProperty("id").GetGuid();
        using var blocked = await admin.Http.GetAsync($"/api/v1/files/{fileId}/content");
        Assert.Equal(HttpStatusCode.Locked, blocked.StatusCode);

        env.Brevo.Attachments["tok-wait"] = Encoding.UTF8.GetBytes("e-post venter " + Guid.NewGuid());
        var subject = $"Venter på skanning {Guid.NewGuid():N}";
        using var anonymous = env.Factory.CreateClient();
        using var delivered = await anonymous.PostAsync(
            $"/api/v1/inbound/brevo?token={WebhookToken}",
            Json(BrevoPayload(address, "<wait@x>", subject, ("a.pdf", "tok-wait", "application/pdf"))));
        Assert.Equal(HttpStatusCode.OK, delivered.StatusCode);

        // Not announced until it has been scanned
        var item = Assert.Single(await ItemsAsync(admin, subject));
        Assert.Equal("scanning", item.GetProperty("status").GetString());
        var itemId = item.GetProperty("id").GetGuid();
        using (var events = JsonDocument.Parse(await admin.Http.GetStringAsync("/api/v1/events?name=core.inbox.received@1&top=200")))
        {
            Assert.DoesNotContain(events.RootElement.EnumerateArray(), e => e.GetProperty("aggregateId").GetString() == itemId.ToString("D"));
        }

        env.Scanner.Unavailable = false;
        var worker = env.Factory.Services.GetServices<IHostedService>().OfType<FileScanWorker>().Single();
        Assert.True(await worker.RunOnceAsync(CancellationToken.None) >= 2);

        var released = Assert.Single(await ItemsAsync(admin, subject));
        Assert.Equal("new", released.GetProperty("status").GetString());
        using var events2 = JsonDocument.Parse(await admin.Http.GetStringAsync("/api/v1/events?name=core.inbox.received@1&top=200"));
        Assert.Single(events2.RootElement.EnumerateArray(), e => e.GetProperty("aggregateId").GetString() == itemId.ToString("D"));
        Assert.NotEmpty(await DownloadAsync(admin, fileId));
    }

    [Fact]
    public async Task InboxItems_AreClaimedByExactlyOne_AndOnlyTheClaimantCanFinishThem()
    {
        await using var env = CreateEnv();
        var admin = await LoginAsync(env.Factory, null);
        var otherAdmin = await LoginAsync(env.Factory, await CreateUserAsync("annen", "tenant.admin"));
        var (inboxId, _) = await CreateInboxAsync(admin, "upload", "Manuell");

        using var created = await UploadItemAsync(admin, inboxId, "Manuell post", "bilag.pdf", Encoding.UTF8.GetBytes("manuell " + Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var itemId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        var results = await Task.WhenAll(
            admin.Http.PostAsync($"/api/v1/inbox/items/{itemId}/claim", null),
            otherAdmin.Http.PostAsync($"/api/v1/inbox/items/{itemId}/claim", null));
        Assert.Equal(
            [HttpStatusCode.OK, HttpStatusCode.Conflict],
            results.Select(r => r.StatusCode).Order().ToArray());

        var winner = results[0].StatusCode == HttpStatusCode.OK ? admin : otherAdmin;
        var loser = winner == admin ? otherAdmin : admin;

        using var wrongComplete = await loser.Http.PostAsJsonAsync($"/api/v1/inbox/items/{itemId}/complete", new { });
        Assert.Equal(HttpStatusCode.Conflict, wrongComplete.StatusCode);

        using var release = await winner.Http.PostAsync($"/api/v1/inbox/items/{itemId}/release", null);
        Assert.Equal(HttpStatusCode.OK, release.StatusCode);
        using var reclaim = await loser.Http.PostAsync($"/api/v1/inbox/items/{itemId}/claim", null);
        Assert.Equal(HttpStatusCode.OK, reclaim.StatusCode);
        using var complete = await loser.Http.PostAsJsonAsync($"/api/v1/inbox/items/{itemId}/complete", new { });
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        Assert.Equal("processed", JsonDocument.Parse(await complete.Content.ReadAsStringAsync()).RootElement.GetProperty("status").GetString());

        using var rejectProcessed = await winner.Http.PostAsJsonAsync($"/api/v1/inbox/items/{itemId}/reject", new { reason = "for sent" });
        Assert.Equal(HttpStatusCode.Conflict, rejectProcessed.StatusCode);
    }

    [Fact]
    public async Task Apps_ClaimInboxItemsAndReadDocuments_OverGrpc_WithTheCallingUsersPermissions()
    {
        await using var env = CreateEnv();
        var admin = await LoginAsync(env.Factory, null);
        var (inboxId, _) = await CreateInboxAsync(admin, "upload", "App-innboks");
        var content = Encoding.UTF8.GetBytes("faktura for app " + Guid.NewGuid());
        using var created = await UploadItemAsync(admin, inboxId, "Til app", "faktura.pdf", content);
        var item = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement;
        var itemId = item.GetProperty("id").GetGuid();
        var fileId = item.GetProperty("fileIds")[0].GetGuid();

        using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = env.Factory.Server.CreateHandler() });
        var client = new DocumentService.DocumentServiceClient(channel);

        CallContextProto Context(params string[] roles)
        {
            using var scope = env.Factory.Services.CreateScope();
            var token = scope.ServiceProvider.GetRequiredService<IInternalTokenIssuer>().IssueShortLivedCallToken(
                DemoTenantSeeder.DemoTenantId, DemoTenantSeeder.DemoUserId, DemoTenantSeeder.DemoCompanyId, roles);
            return new CallContextProto { TenantId = DemoTenantSeeder.DemoTenantId.ToString("D"), AccessToken = token };
        }

        InboxItemActionRequest Action(CallContextProto ctx) => new() { Context = ctx, ItemId = itemId.ToString("D"), Claimant = "finance.invoicereader" };

        // A user without document permissions cannot read through an app either
        var denied = await Assert.ThrowsAsync<RpcException>(async () =>
            await client.GetDocumentAsync(new GetDocumentRequest { Context = Context("demo.ingen"), FileId = fileId.ToString("D") }));
        Assert.Equal(StatusCode.PermissionDenied, denied.StatusCode);

        var ctx = Context("tenant.admin");
        var document = await client.GetDocumentAsync(new GetDocumentRequest { Context = ctx, FileId = fileId.ToString("D") });
        Assert.True(document.Found);
        Assert.Equal("faktura.pdf", document.FileName);
        Assert.Equal(content, document.Content.ToByteArray());
        Assert.False((await client.GetDocumentAsync(new GetDocumentRequest { Context = ctx, FileId = Guid.NewGuid().ToString("D") })).Found);

        var claimed = await client.ClaimInboxItemAsync(Action(ctx));
        Assert.Equal("claimed", claimed.Status);
        Assert.Equal("app:finance.invoicereader", claimed.ClaimedBy);
        Assert.Equal([fileId.ToString("D")], claimed.FileIds);

        var second = await Assert.ThrowsAsync<RpcException>(async () => await client.ClaimInboxItemAsync(Action(ctx)));
        Assert.Equal(StatusCode.Aborted, second.StatusCode);

        var completeRequest = Action(ctx);
        completeRequest.ResultObjectId = "core.party";
        completeRequest.ResultRecordId = Guid.NewGuid().ToString("D");
        var completed = await client.CompleteInboxItemAsync(completeRequest);
        Assert.Equal("processed", completed.Status);
    }

    [Fact]
    public async Task DocumentAccess_FollowsRoles()
    {
        await using var env = CreateEnv();
        var role = $"demo.dokumenter-{Guid.NewGuid():N}";
        var admin = await LoginAsync(env.Factory, null);
        var clerk = await LoginAsync(env.Factory, await CreateUserAsync("saksbehandler", role));
        var (inboxId, _) = await CreateInboxAsync(admin, "upload", "Roller");
        using var seeded = await UploadItemAsync(admin, inboxId, "Seed", "s.pdf", Encoding.UTF8.GetBytes("seed " + Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Created, seeded.StatusCode);

        using var denied = await clerk.Http.GetAsync("/api/v1/inbox/items");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        await SetRoleAsync(admin, role, "core.documents.reader");
        using var read = await clerk.Http.GetAsync("/api/v1/inbox/items");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var cannotCreate = await UploadAsync(clerk, "nei.pdf", Encoding.UTF8.GetBytes("nei " + Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Forbidden, cannotCreate.StatusCode);
        using var cannotClaim = await clerk.Http.PostAsync($"/api/v1/inbox/items/{Guid.NewGuid()}/claim", null);
        Assert.Equal(HttpStatusCode.Forbidden, cannotClaim.StatusCode);

        await SetRoleAsync(admin, role, "core.documents.user");
        using var canCreate = await UploadAsync(clerk, "ja.pdf", Encoding.UTF8.GetBytes("ja " + Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Created, canCreate.StatusCode);
    }

    [Fact]
    public async Task PdfIsGeneratedFromAnHtmlTemplate_WithEncodedData_StoredAndSentByEmail()
    {
        await using var env = CreateEnv();
        var admin = await LoginAsync(env.Factory, null);

        using var res = await admin.Http.PostAsJsonAsync(
            "/api/v1/documents/render",
            new
            {
                template = "<p>{{customer.name}}: {{amount}} {{missing}}</p>",
                data = new { customer = new { name = "A <b>&</b> Co" }, amount = 1250.5m },
                fileName = $"faktura-{Guid.NewGuid():N}",
                email = new { to = "kunde@example.no", subject = "Din faktura", body = "Vedlagt." }
            });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var file = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("application/pdf", file.GetProperty("contentType").GetString());
        Assert.EndsWith(".pdf", file.GetProperty("fileName").GetString());
        Assert.Equal("generated", file.GetProperty("source").GetString());

        var pdf = await DownloadAsync(admin, file.GetProperty("id").GetGuid());
        Assert.Contains("<p>A &lt;b&gt;&amp;&lt;/b&gt; Co: 1250.5 </p>", Encoding.UTF8.GetString(pdf));

        var mail = Assert.Single(env.Email.Sent);
        Assert.Equal("kunde@example.no", mail.To);
        var attachment = Assert.Single(mail.Attachments!);
        Assert.Equal(file.GetProperty("fileName").GetString(), attachment.FileName);
        Assert.Equal(pdf, attachment.Content);
    }

    [Fact]
    public void BrevoAdapter_ReadsThePayloadWhateverTheCasing_AndSkipsAttachmentsWithoutToken()
    {
        using var doc = JsonDocument.Parse(
            """
            { "items": [ {
                "messageid": "<abc@x>", "from": { "address": "a@b.no", "name": "Ola" },
                "to": [ { "address": "x@inbox.no" } ], "cc": [ { "Address": "y@inbox.no" } ],
                "subject": "Hei", "rawtextbody": "Tekst", "sentatdate": "2026-10-30T10:00:00Z",
                "attachments": [ { "name": "a.pdf", "contenttype": "application/pdf", "contentlength": 10, "downloadtoken": "t1" },
                                 { "name": "uten-token.pdf" } ] } ] }
            """);

        var email = Assert.Single(BrevoInboundAdapter.Parse(doc.RootElement, new FakeBrevo()));
        Assert.Equal("a@b.no", email.From);
        Assert.Equal("Ola", email.FromName);
        Assert.Equal(["x@inbox.no", "y@inbox.no"], email.Recipients);
        Assert.Equal("<abc@x>", email.MessageId);
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 10, 0, 0, TimeSpan.Zero), email.ReceivedAt);
        var attachment = Assert.Single(email.Attachments);
        Assert.Equal("a.pdf", attachment.FileName);
        Assert.Equal(10, attachment.SizeHint);
    }

    // ------------------------------------------------------------------ ClamAV protocol

    [Fact]
    public async Task ClamAvScanner_SpeaksTheInstreamProtocol_AndFailsClosedWhenUnreachable()
    {
        var content = new byte[150_000];
        Random.Shared.NextBytes(content);

        foreach (var (reply, outcome, signature) in new[]
                 {
                     ("stream: OK", ScanOutcome.Clean, (string?)null),
                     ("stream: Eicar-Test-Signature FOUND", ScanOutcome.Infected, "Eicar-Test-Signature"),
                     ("INSTREAM size limit exceeded. ERROR", ScanOutcome.Unavailable, null)
                 })
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync();
                await using var stream = client.GetStream();
                var command = new byte[10];
                await stream.ReadExactlyAsync(command);
                var received = new MemoryStream();
                var length = new byte[4];
                while (true)
                {
                    await stream.ReadExactlyAsync(length);
                    var size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(length);
                    if (size == 0)
                    {
                        break;
                    }

                    var chunk = new byte[size];
                    await stream.ReadExactlyAsync(chunk);
                    received.Write(chunk);
                }

                await stream.WriteAsync(Encoding.ASCII.GetBytes(reply + "\0"));
                return (Command: Encoding.ASCII.GetString(command), Received: received.ToArray());
            });

            var result = await new ClamAvScanner(new ClamAvOptions { Host = "127.0.0.1", Port = port, Timeout = TimeSpan.FromSeconds(10) })
                .ScanAsync(content, CancellationToken.None);
            var seen = await server;

            Assert.Equal("zINSTREAM\0", seen.Command);
            Assert.Equal(content, seen.Received);
            Assert.Equal(outcome, result.Outcome);
            Assert.Equal(signature, result.Signature);
        }

        // Nothing listening: the scanner reports 'unavailable', never 'clean'
        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var closedPort = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        var unreachable = await new ClamAvScanner(new ClamAvOptions { Host = "127.0.0.1", Port = closedPort, Timeout = TimeSpan.FromSeconds(5) })
            .ScanAsync(content, CancellationToken.None);
        Assert.Equal(ScanOutcome.Unavailable, unreachable.Outcome);
    }

    // ------------------------------------------------------------------ helpers

    private static Dictionary<string, object?> Party(string number) => new()
    {
        ["partyNumber"] = number,
        ["name"] = "Dokument AS",
        ["partyType"] = "organization",
        ["role"] = "supplier",
        ["organizationNumber"] = "955111222"
    };

    private static StringContent Json(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    /// <summary>A webhook body shaped like Brevo's inbound-parse payload.</summary>
    private static object BrevoPayload(string to, string messageId, string subject, params (string Name, string Token, string Type)[] attachments) => new
    {
        items = new[]
        {
            new
            {
                MessageId = messageId,
                From = new { Name = "Leverandør AS", Address = "kunde@leverandor.no" },
                To = new[] { new { Name = "Innboks", Address = to } },
                Subject = subject,
                RawTextBody = "Se vedlagt faktura.",
                SentAtDate = "2026-10-30T09:00:00Z",
                Attachments = attachments.Select(a => new { Name = a.Name, ContentType = a.Type, ContentLength = 100, DownloadToken = a.Token }).ToArray()
            }
        }
    };

    private static async Task<(Guid Id, string Address)> CreateInboxAsync(Session admin, string channel, string name)
    {
        using var res = await admin.Http.PostAsJsonAsync("/api/v1/inboxes", new { channel, name });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var inbox = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        return (inbox.GetProperty("id").GetGuid(), inbox.TryGetProperty("address", out var a) ? a.GetString() ?? string.Empty : string.Empty);
    }

    private static async Task<List<JsonElement>> ItemsAsync(Session session, string subject)
    {
        using var doc = JsonDocument.Parse(await session.Http.GetStringAsync("/api/v1/inbox/items?top=200"));
        return doc.RootElement.EnumerateArray().Where(i => i.GetProperty("subject").GetString() == subject).Select(i => i.Clone()).ToList();
    }

    private static async Task<int> InboxAttentionCountAsync(Session session)
    {
        using var doc = JsonDocument.Parse(await session.Http.GetStringAsync("/api/v1/attention"));
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.GetProperty("id").GetString() == "core.inbox.new")
            {
                return item.GetProperty("count").GetInt32();
            }
        }

        return 0;
    }

    private static async Task<byte[]> DownloadAsync(Session session, Guid fileId)
    {
        using var res = await session.Http.GetAsync($"/api/v1/files/{fileId}/content");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadAsByteArrayAsync();
    }

    private static Task<HttpResponseMessage> UploadAsync(Session session, string fileName, byte[] content)
    {
        var form = new MultipartFormDataContent { { new ByteArrayContent(content), "file", fileName } };
        return session.Http.PostAsync("/api/v1/files", form);
    }

    private static Task<HttpResponseMessage> UploadItemAsync(Session session, Guid inboxId, string subject, string fileName, byte[] content)
    {
        var form = new MultipartFormDataContent { { new ByteArrayContent(content), "file", fileName }, { new StringContent(subject), "subject" } };
        return session.Http.PostAsync($"/api/v1/inboxes/{inboxId}/items", form);
    }

    private static async Task SetRoleAsync(Session admin, string role, params string[] permissionSets)
    {
        using var res = await admin.Http.PutAsJsonAsync($"/api/v1/roles/{role}/permission-sets", new { permissionSets });
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
    }

    private async Task<string> StorageKeyAsync(Guid fileId)
    {
        await using var migrator = new NpgsqlConnection(fixture.MigrationConnectionString);
        await migrator.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT storage_key FROM core.file WHERE id = @id;", migrator);
        cmd.Parameters.AddWithValue("id", fileId);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<Session> LoginAsync(WebApplicationFactory<Program> factory, string? subject)
    {
        var http = factory.CreateClient();
        var api = new ErpApiClient(http, new I18nService());
        Assert.NotNull(await api.DevLoginAsync(subject));
        return new Session(http, api);
    }

    private async Task<string> CreateUserAsync(string name, string role)
    {
        var subject = $"{name}-{Guid.NewGuid():N}"[..24];
        var tenantContext = new TenantContext();
        tenantContext.Set(DemoTenantSeeder.DemoTenantId, DemoTenantSeeder.DemoUserId);
        await using var db = fixture.CreateAppDbContext(tenantContext);
        db.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            TenantId = DemoTenantSeeder.DemoTenantId,
            CompanyId = DemoTenantSeeder.DemoCompanyId,
            ExternalSubject = subject,
            Email = $"{subject}@demohandel.no",
            DisplayName = name,
            Roles = [role, "core.user"],
            AccessibleCompanyIds = [DemoTenantSeeder.DemoCompanyId],
            IsActive = true
        });
        await db.SaveChangesAsync();
        _createdSubjects.Add(subject);
        return subject;
    }

    private Env CreateEnv()
    {
        var storage = new InMemoryObjectStorage();
        var scanner = new FakeScanner();
        var brevo = new FakeBrevo();
        var email = new CapturingEmailSender();
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
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
                    ["Email:SmtpHost"] = "",
                    ["Documents:InboxDomain"] = "inbox.test.local",
                    ["Documents:Retention:NO:bilag"] = "7",
                    ["Brevo:WebhookToken"] = WebhookToken
                });
            });

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IObjectStorage>();
                services.AddSingleton<IObjectStorage>(storage);
                services.RemoveAll<IVirusScanner>();
                services.AddSingleton<IVirusScanner>(scanner);
                services.RemoveAll<IBrevoAttachmentClient>();
                services.AddSingleton<IBrevoAttachmentClient>(brevo);
                services.RemoveAll<IPdfRenderer>();
                services.AddScoped<IPdfRenderer, FakePdfRenderer>();
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender>(email);
            });
        });

        return new Env(factory, scanner, brevo, email);
    }
}
