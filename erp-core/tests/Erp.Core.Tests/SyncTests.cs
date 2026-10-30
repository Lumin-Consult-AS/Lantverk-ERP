using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Demo.Contacts;
using Erp.Client.Models;
using Erp.Client.Services;
using Erp.Core.Domain.Entities;
using Erp.Core.Domain.Tenancy;
using Erp.Core.Infrastructure.Apps;
using Erp.Core.Infrastructure.Persistence;
using Erp.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Erp.Core.Tests;

[Collection(PostgresDatabaseCollection.Name)]
public sealed class SyncTests(PostgresDatabaseFixture fixture) : IAsyncLifetime
{
    private const string TestOidcSigningKey = "test-only-external-oidc-signing-key-at-least-32-bytes!";
    private const string TestOidcIssuer = "https://oidc.test.local/realms/erp-dev";
    private const string TestOidcAudience = "erp-web";
    private const string NotesSet = "demo.contacts.notes";

    private readonly List<string> _createdSubjects = [];

    public Task InitializeAsync() => Task.CompletedTask;

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

    private sealed class Session(HttpClient http, ErpApiClient api)
    {
        public HttpClient Http { get; } = http;
        public ErpApiClient Api { get; } = api;
    }

    private sealed record Env(WebApplicationFactory<Program> Factory, WebApplication Host, Session Admin) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            await Factory.DisposeAsync();
        }
    }

    // ------------------------------------------------------------------ the demo: download, offline, reconnect, conflicts

    [Fact]
    public async Task OfflineClient_DownloadsASet_GoesOffline_AndItsCommandsAreProcessedWithTheSameRulesAsOnline()
    {
        await using var env = await SetUpAsync();
        var admin = env.Admin;
        var factory = env.Factory;

        try
        {
            var party = await admin.Api.CreateRecordAsync("core.party", Party("P-SYNC-1"));
            var n1 = await NoteAsync(admin, party, "Notat 1");
            var n2 = await NoteAsync(admin, party, "Notat 2");
            var n3 = await NoteAsync(admin, party, "Notat 3");
            var n5 = await NoteAsync(admin, party, "Notat 5");
            var n6 = await NoteAsync(admin, party, "Notat 6");

            var deviceA = await RegisterDeviceAsync(admin, "Terminal A");
            var deviceB = await RegisterDeviceAsync(admin, "Terminal B");

            using var sets = Json(await admin.Http.GetStringAsync("/api/v1/sync/sets"));
            var set = sets.RootElement.EnumerateArray().Single(s => s.GetProperty("id").GetString() == NotesSet);
            Assert.Contains("demo.contactnote", set.GetProperty("objects").EnumerateArray().Select(o => o.GetString()));

            // 1. Download: a snapshot of the whole set, in pages, ending with the cursor for later delta pulls
            var (downloaded, cursor, pages) = await PullSnapshotAsync(admin, deviceA, limit: 2);
            Assert.True(pages >= 3, "a small page size must give several pages");
            foreach (var id in new[] { n1.Id, n2.Id, n3.Id, n5.Id, n6.Id, party.Id })
            {
                Assert.Contains(id, downloaded);
            }

            Assert.False(string.IsNullOrWhiteSpace(cursor));

            // 2. Reserve what the device will work on; another device cannot take the same records, a reservation expires
            Assert.Equal(HttpStatusCode.OK, (await ReserveAsync(admin, deviceA, [n1.Id, n2.Id])).StatusCode);
            using (var taken = await ReserveAsync(admin, deviceB, [n1.Id]))
            {
                Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
                var conflict = Json(await taken.Content.ReadAsStringAsync()).RootElement.GetProperty("conflicts")[0];
                Assert.Equal("reserved", conflict.GetProperty("reason").GetString());
                Assert.Equal(deviceA, conflict.GetProperty("heldByDevice").GetGuid());
            }

            Assert.Equal(HttpStatusCode.OK, (await ReserveAsync(admin, deviceB, [n6.Id, n3.Id])).StatusCode);
            await ExpireReservationAsync(n3.Id);
            Assert.Equal(HttpStatusCode.OK, (await ReserveAsync(admin, deviceA, [n3.Id])).StatusCode);

            // 3. The device goes offline. Meanwhile the data changes online
            await admin.Api.UpdateRecordAsync("demo.contactnote", n2.Id, n2.RowVersion, new Dictionary<string, object?> { ["priority"] = "high" });
            await admin.Api.UpdateRecordAsync("demo.contactnote", n5.Id, n5.RowVersion, new Dictionary<string, object?> { ["text"] = "Endret på serveren" });
            var n4 = await NoteAsync(admin, party, "Notat 4");
            await admin.Api.DeleteRecordAsync("demo.contactnote", n3.Id, n3.RowVersion);

            // 4. Back online: a delta pull returns what changed, what was deleted, and nothing else
            var delta = await PullDeltaAsync(admin, deviceA, cursor!);
            Assert.Contains(n2.Id, delta.Records);
            Assert.Contains(n4.Id, delta.Records);
            Assert.Contains(n5.Id, delta.Records);
            Assert.DoesNotContain(n1.Id, delta.Records);
            Assert.DoesNotContain(n6.Id, delta.Records);
            Assert.Contains(delta.Removed, r => r.Id == n3.Id && r.Reason == "deleted");
            Assert.NotEqual(cursor, delta.Cursor);

            var again = await PullDeltaAsync(admin, deviceA, delta.Cursor!);
            Assert.Empty(again.Records);
            Assert.Empty(again.RemovedList);
            Assert.Equal(delta.Cursor, again.Cursor);

            // 5. The queued commands are uploaded in order. Each rule is the app's: adjust, reject, or a manual queue
            var yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1).ToString("yyyy-MM-dd");
            var c1 = Guid.NewGuid();
            var c2 = Guid.NewGuid();
            var c3 = Guid.NewGuid();
            var c4 = Guid.NewGuid();
            var c5 = Guid.NewGuid();
            var c6 = Guid.NewGuid();
            var c7 = Guid.NewGuid();
            var n6Now = (await admin.Api.GetRecordAsync("demo.contactnote", n6.Id))!;

            var results = await PushAsync(admin, deviceA,
            [
                Command(c1, "demo.contacts.setpriority", new { priority = "high" }, (n1.Id, n1.RowVersion)),
                Command(c3, "demo.contacts.setfollowup", new { followUp = yesterday }, (n1.Id, n1.RowVersion)),
                Command(c2, "demo.contacts.setpriority", new { priority = "low" }, (n2.Id, n2.RowVersion)),
                Command(c4, "demo.contacts.deletenote", new { }, (n5.Id, n5.RowVersion)),
                Command(c5, "demo.contacts.setpriority", new { priority = "low" }, (n6.Id, n6Now.RowVersion)),
                Command(c6, "demo.contacts.nope", new { }),
                Command(c7, "demo.contacts.deletenote", new { }, (n2.Id, n2.RowVersion))
            ]);

            // c1: nothing changed on the server -> applied
            Assert.Equal("applied", results[0].GetProperty("status").GetString());
            Assert.Equal("high", await PriorityAsync(admin, n1.Id));

            // c3: the same record again. The core knows c1 changed it, so this is not a conflict; the app's normal validation
            // (no follow-up date in the past) rejects it, exactly as it does for an online user
            Assert.Equal("rejected", results[1].GetProperty("status").GetString());
            Assert.Contains("Oppfølgingsdato kan ikke være tilbake i tid", results[1].GetProperty("message").GetString());

            // c2: the note was changed on the server (adjust rule) -> the app keeps the higher priority and says so
            Assert.Equal("adjusted", results[2].GetProperty("status").GetString());
            Assert.Contains("beholdt", results[2].GetProperty("message").GetString());
            Assert.Equal("high", await PriorityAsync(admin, n2.Id));
            Assert.Equal("stale", results[2].GetProperty("conflicts")[0].GetProperty("reason").GetString());

            // c4 / c7: changed on the server and the rule is "manual" -> they wait for a person
            Assert.Equal("manual", results[3].GetProperty("status").GetString());
            Assert.Equal("manual", results[6].GetProperty("status").GetString());
            Assert.NotNull(await admin.Api.GetRecordAsync("demo.contactnote", n5.Id));

            // c5: reserved by another device -> a conflict whatever the rule says; c6: unknown command
            Assert.Equal("conflict", results[4].GetProperty("status").GetString());
            Assert.Equal("reserved", results[4].GetProperty("conflicts")[0].GetProperty("reason").GetString());
            Assert.Equal("rejected", results[5].GetProperty("status").GetString());

            // 6. Sending c1 again (the answer was lost) returns the stored answer and does not run it twice
            var versionBefore = (await admin.Api.GetRecordAsync("demo.contactnote", n1.Id))!.RowVersion;
            var resent = await PushAsync(admin, deviceA, [Command(c1, "demo.contacts.setpriority", new { priority = "high" }, (n1.Id, n1.RowVersion))]);
            Assert.Equal("applied", resent[0].GetProperty("status").GetString());
            Assert.True(resent[0].GetProperty("duplicate").GetBoolean());
            Assert.Equal(versionBefore, (await admin.Api.GetRecordAsync("demo.contactnote", n1.Id))!.RowVersion);

            // 7. The manual queue: a person applies one command and discards the other
            using var queue = Json(await admin.Http.GetStringAsync("/api/v1/sync/commands?status=manual"));
            var queuedIds = queue.RootElement.EnumerateArray().Select(c => c.GetProperty("clientCommandId").GetGuid()).ToList();
            Assert.Contains(c4, queuedIds);
            Assert.Contains(c7, queuedIds);
            var row4 = queue.RootElement.EnumerateArray().Single(c => c.GetProperty("clientCommandId").GetGuid() == c4).GetProperty("rowId").GetGuid();
            var row7 = queue.RootElement.EnumerateArray().Single(c => c.GetProperty("clientCommandId").GetGuid() == c7).GetProperty("rowId").GetGuid();

            using var apply = await admin.Http.PostAsJsonAsync($"/api/v1/sync/commands/{row4}/resolve", new { action = "apply" });
            Assert.Equal(HttpStatusCode.OK, apply.StatusCode);
            Assert.Equal("applied", Json(await apply.Content.ReadAsStringAsync()).RootElement.GetProperty("status").GetString());
            Assert.Null(await admin.Api.GetRecordAsync("demo.contactnote", n5.Id));

            using var discard = await admin.Http.PostAsJsonAsync($"/api/v1/sync/commands/{row7}/resolve", new { action = "discard" });
            Assert.Equal("discarded", Json(await discard.Content.ReadAsStringAsync()).RootElement.GetProperty("status").GetString());
            Assert.NotNull(await admin.Api.GetRecordAsync("demo.contactnote", n2.Id));

            using var resolvedTwice = await admin.Http.PostAsJsonAsync($"/api/v1/sync/commands/{row4}/resolve", new { action = "apply" });
            Assert.Equal(HttpStatusCode.Conflict, resolvedTwice.StatusCode);

            // 8. A blocked device is shut out centrally; unblocking lets it back in
            using var block = await admin.Http.PostAsJsonAsync($"/api/v1/sync/devices/{deviceB}/block", new { reason = "Mistet terminal" });
            Assert.Equal(HttpStatusCode.OK, block.StatusCode);
            foreach (var response in new[]
                     {
                         await admin.Http.PostAsJsonAsync("/api/v1/sync/pull", new { deviceId = deviceB, setId = NotesSet }),
                         await admin.Http.PostAsJsonAsync("/api/v1/sync/push", new { deviceId = deviceB, commands = Array.Empty<object>() }),
                         await ReserveAsync(admin, deviceB, [n1.Id]),
                         await admin.Http.PostAsync($"/api/v1/sync/devices/{deviceB}/token", null)
                     })
            {
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                Assert.Equal("device-blocked", Json(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
                response.Dispose();
            }

            using var unblock = await admin.Http.PostAsync($"/api/v1/sync/devices/{deviceB}/unblock", null);
            Assert.Equal(HttpStatusCode.OK, unblock.StatusCode);
            using var back = await admin.Http.PostAsJsonAsync("/api/v1/sync/pull", new { deviceId = deviceB, setId = NotesSet });
            Assert.Equal(HttpStatusCode.OK, back.StatusCode);

            // 9. On reconnecting the device gets a fresh short-lived token, which works against the API
            using var tokenRes = await admin.Http.PostAsync($"/api/v1/sync/devices/{deviceA}/token", null);
            Assert.Equal(HttpStatusCode.OK, tokenRes.StatusCode);
            var token = Json(await tokenRes.Content.ReadAsStringAsync()).RootElement;
            Assert.True(token.GetProperty("expiresAt").GetDateTimeOffset() > DateTimeOffset.UtcNow);
            using var deviceHttp = factory.CreateClient();
            deviceHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.GetProperty("token").GetString());
            using var session = await deviceHttp.GetAsync("/api/v1/session");
            Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        }
        finally
        {
            await TearDownAsync(admin);
        }
    }

    // ------------------------------------------------------------------ the cursor never skips a late commit

    [Fact]
    public async Task TheCursor_NeverMovesPastAChangeThatCommitsLate()
    {
        await using var env = await SetUpAsync();
        var admin = env.Admin;

        try
        {
            var party = await admin.Api.CreateRecordAsync("core.party", Party("P-SYNC-2"));
            var early = await NoteAsync(admin, party, "Tidlig");
            var late = await NoteAsync(admin, party, "Sen");
            var device = await RegisterDeviceAsync(admin, "Terminal");
            var (_, cursor, _) = await PullSnapshotAsync(admin, device, limit: 500);

            // A transaction that touches one note and stays open ...
            await using var slow = new NpgsqlConnection(fixture.MigrationConnectionString);
            await slow.OpenAsync();
            await using var tx = await slow.BeginTransactionAsync();
            await using (var touch = new NpgsqlCommand("""UPDATE "demo"."contactnote" SET row_version = row_version + 1 WHERE id = @id;""", slow, tx))
            {
                touch.Parameters.AddWithValue("id", late.Id);
                await touch.ExecuteNonQueryAsync();
            }

            // ... while another change commits normally, after it started
            await admin.Api.UpdateRecordAsync("demo.contactnote", early.Id, early.RowVersion, new Dictionary<string, object?> { ["priority"] = "low" });

            // Nothing is handed out beyond the open transaction, and the cursor stays where it was
            var during = await PullDeltaAsync(admin, device, cursor!);
            Assert.Empty(during.Records);
            Assert.Equal(cursor, during.Cursor);

            await tx.CommitAsync();

            // Now both changes arrive: the late commit was not skipped
            var after = await PullDeltaAsync(admin, device, cursor!);
            Assert.Contains(late.Id, after.Records);
            Assert.Contains(early.Id, after.Records);
        }
        finally
        {
            await TearDownAsync(admin);
        }
    }

    // ------------------------------------------------------------------ ownership and permissions

    [Fact]
    public async Task Devices_BelongToTheirUser_AndTheSetIsOnlyReadableWithPermissionToItsObjects()
    {
        await using var env = await SetUpAsync();
        var admin = env.Admin;
        var role = $"demo.synk-{Guid.NewGuid():N}";

        try
        {
            var adminDevice = await RegisterDeviceAsync(admin, "Admin-terminal");
            var clerk = await LoginAsync(env.Factory, await CreateUserAsync("lager", role));

            // Another user's device cannot be used
            using var foreign = await clerk.Http.PostAsJsonAsync("/api/v1/sync/pull", new { deviceId = adminDevice, setId = NotesSet });
            Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);

            // Their own device, but no permission to read the objects of the set: enforced by the data API, as online
            var clerkDevice = await RegisterDeviceAsync(clerk, "Lager-terminal");
            using var denied = await clerk.Http.PostAsJsonAsync("/api/v1/sync/pull", new { deviceId = clerkDevice, setId = NotesSet });
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

            using var notesOnly = await admin.Http.PutAsJsonAsync($"/api/v1/roles/{role}/permission-sets", new { permissionSets = new[] { "demo.contacts.user" } });
            Assert.Equal(HttpStatusCode.NoContent, notesOnly.StatusCode);
            using var stillDenied = await clerk.Http.PostAsJsonAsync("/api/v1/sync/pull", new { deviceId = clerkDevice, setId = NotesSet });
            Assert.Equal(HttpStatusCode.Forbidden, stillDenied.StatusCode); // the set also contains parties

            using var both = await admin.Http.PutAsJsonAsync($"/api/v1/roles/{role}/permission-sets", new { permissionSets = new[] { "demo.contacts.user", "core.parties.reader" } });
            Assert.Equal(HttpStatusCode.NoContent, both.StatusCode);
            using var allowed = await clerk.Http.PostAsJsonAsync("/api/v1/sync/pull", new { deviceId = clerkDevice, setId = NotesSet });
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

            // Blocking is an administrator's job; a device owner cannot unblock their own device
            using var selfBlock = await clerk.Http.PostAsJsonAsync($"/api/v1/sync/devices/{clerkDevice}/block", new { });
            Assert.Equal(HttpStatusCode.Forbidden, selfBlock.StatusCode);
            using var queue = await clerk.Http.GetAsync("/api/v1/sync/commands");
            Assert.Equal(HttpStatusCode.Forbidden, queue.StatusCode);

            // Unknown set, malformed cursor
            using var unknown = await admin.Http.PostAsJsonAsync("/api/v1/sync/pull", new { deviceId = adminDevice, setId = "demo.contacts.nope" });
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            using var badCursor = await admin.Http.PostAsJsonAsync("/api/v1/sync/pull", new { deviceId = adminDevice, setId = NotesSet, cursor = "abc" });
            Assert.Equal(HttpStatusCode.BadRequest, badCursor.StatusCode);
        }
        finally
        {
            await TearDownAsync(admin);
        }
    }

    // ------------------------------------------------------------------ manifest rules

    [Fact]
    public async Task Manifests_WithInvalidSyncSetsOrCommands_AreRefused()
    {
        await using var factory = CreateFactory();
        var admin = await LoginAsync(factory, null);

        foreach (var (label, yaml, expected) in new[]
                 {
                     ("a command in a declarative app",
                      "id: demo.bad1\nversion: 1.0.0\nname: Ugyldig\ncommands:\n  - id: demo.bad1.do\n    handler: Do\n",
                      "cannot declare commands"),
                     ("an unknown conflict rule",
                      "id: demo.bad2\nversion: 1.0.0\nname: Ugyldig\nruntime:\n  image: ghcr.io/example/bad:1.0.0\ncommands:\n  - id: demo.bad2.do\n    handler: Do\n    onConflict: explode\n",
                      "unknown conflict rule"),
                     ("a command without a handler",
                      "id: demo.bad3\nversion: 1.0.0\nname: Ugyldig\nruntime:\n  image: ghcr.io/example/bad:1.0.0\ncommands:\n  - id: demo.bad3.do\n",
                      "must name its handler"),
                     ("a sync set over an unknown object",
                      "id: demo.bad4\nversion: 1.0.0\nname: Ugyldig\nsyncSets:\n  - id: demo.bad4.set\n    objects:\n      - object: demo.nothing\n",
                      "unknown object"),
                     ("a sync set with an invalid filter",
                      "id: demo.bad5\nversion: 1.0.0\nname: Ugyldig\nobjects:\n  demo.bad5.thing:\n    label: T\n    fields:\n      name: { type: string }\nsyncSets:\n  - id: demo.bad5.set\n    objects:\n      - object: demo.bad5.thing\n        filter: \"name ==== 'x'\"\n",
                      "invalid filter"),
                     ("a sync set with another app's prefix",
                      "id: demo.bad6\nversion: 1.0.0\nname: Ugyldig\nobjects:\n  demo.bad6.thing:\n    label: T\n    fields:\n      name: { type: string }\nsyncSets:\n  - id: other.app.set\n    objects:\n      - object: demo.bad6.thing\n",
                      "prefix")
                 })
        {
            var zip = AppPackageFormat.CreateSignedZipFromFiles(new Dictionary<string, string> { ["app.yaml"] = yaml });
            var content = new ByteArrayContent(zip);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            using var res = await admin.Http.PostAsync("/api/v1/apps/install", content);
            Assert.True(res.StatusCode == HttpStatusCode.BadRequest, $"{label}: expected 400, got {(int)res.StatusCode}");
            Assert.Contains(expected, await res.Content.ReadAsStringAsync());
        }
    }

    // ------------------------------------------------------------------ helpers

    private async Task<Env> SetUpAsync()
    {
        var factory = CreateFactory();
        var host = await StartContainerAsync<ContactsApp>(factory);
        factory.Services.GetRequiredService<KubernetesAppOrchestrator>().RegisterEndpoint(
            "demo.contacts", "1.1.0", "http://demo-contacts:50051", () => host.GetTestServer().CreateHandler());

        var admin = await LoginAsync(factory, null);
        Assert.True(await admin.Api.InstallBuiltInAppAsync("core.parties"));
        Assert.True(await admin.Api.InstallBuiltInAppAsync("demo.contacts"));
        return new Env(factory, host, admin);
    }

    private static async Task TearDownAsync(Session admin)
    {
        await admin.Api.UninstallAppAsync("demo.contacts");
        await admin.Api.UninstallAppAsync("core.parties");
    }

    private static Dictionary<string, object?> Party(string number) => new()
    {
        ["partyNumber"] = number,
        ["name"] = "Synk AS",
        ["partyType"] = "organization",
        ["role"] = "customer",
        ["organizationNumber"] = "955777888"
    };

    private static Task<ClientRecordData> NoteAsync(Session admin, ClientRecordData party, string text) =>
        admin.Api.CreateRecordAsync("demo.contactnote", new Dictionary<string, object?>
        {
            ["party"] = party.Id,
            ["text"] = text,
            ["followUp"] = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(10).ToString("yyyy-MM-dd")
        });

    private static JsonDocument Json(string text) => JsonDocument.Parse(text);

    private static async Task<Guid> RegisterDeviceAsync(Session session, string name)
    {
        using var res = await session.Http.PostAsJsonAsync("/api/v1/sync/devices", new { name });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return Json(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
    }

    private sealed record Pulled(List<Guid> Records, List<(Guid Id, string Reason)> RemovedList, string? Cursor)
    {
        public IEnumerable<(Guid Id, string Reason)> Removed => RemovedList;
    }

    private static async Task<(List<Guid> Records, string? Cursor, int Pages)> PullSnapshotAsync(Session session, Guid device, int limit)
    {
        var records = new List<Guid>();
        string? continuation = null;
        string? cursor = null;
        var pages = 0;
        do
        {
            using var res = await session.Http.PostAsJsonAsync("/api/v1/sync/pull", new { deviceId = device, setId = NotesSet, continuation, limit });
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            using var doc = Json(await res.Content.ReadAsStringAsync());
            records.AddRange(doc.RootElement.GetProperty("records").EnumerateArray().Select(r => r.GetProperty("record").GetProperty("id").GetGuid()));
            pages++;
            continuation = doc.RootElement.GetProperty("continuation").GetString();
            cursor = doc.RootElement.GetProperty("cursor").ValueKind == JsonValueKind.Null ? null : doc.RootElement.GetProperty("cursor").GetString();
            if (!doc.RootElement.GetProperty("hasMore").GetBoolean())
            {
                break;
            }
        }
        while (pages < 100);

        return (records, cursor, pages);
    }

    private static async Task<Pulled> PullDeltaAsync(Session session, Guid device, string cursor)
    {
        using var res = await session.Http.PostAsJsonAsync("/api/v1/sync/pull", new { deviceId = device, setId = NotesSet, cursor });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = Json(await res.Content.ReadAsStringAsync());
        return new Pulled(
            doc.RootElement.GetProperty("records").EnumerateArray().Select(r => r.GetProperty("record").GetProperty("id").GetGuid()).ToList(),
            doc.RootElement.GetProperty("removed").EnumerateArray().Select(r => (r.GetProperty("id").GetGuid(), r.GetProperty("reason").GetString()!)).ToList(),
            doc.RootElement.GetProperty("cursor").GetString());
    }

    private static Task<HttpResponseMessage> ReserveAsync(Session session, Guid device, IReadOnlyList<Guid> ids) =>
        session.Http.PostAsJsonAsync(
            "/api/v1/sync/reservations", new { deviceId = device, objectId = "demo.contactnote", recordIds = ids, ttlMinutes = 60 });

    private static object Command(Guid id, string name, object args, params (Guid Record, long BaseVersion)[] targets) => new
    {
        id,
        name,
        args,
        targets = targets.Select(t => new { objectId = "demo.contactnote", recordId = t.Record, baseRowVersion = t.BaseVersion }).ToArray()
    };

    private static async Task<List<JsonElement>> PushAsync(Session session, Guid device, object[] commands)
    {
        using var res = await session.Http.PostAsJsonAsync("/api/v1/sync/push", new { deviceId = device, commands });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = Json(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("results").EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private static async Task<string?> PriorityAsync(Session session, Guid noteId)
    {
        var note = await session.Api.GetRecordAsync("demo.contactnote", noteId);
        Assert.NotNull(note);
        Assert.True(note.Fields.TryGetValue("priority", out var value), "fields: " + string.Join(",", note.Fields.Keys));
        return I18nService.UnwrapDisplayString(value) ?? "raw:" + value;
    }

    private async Task ExpireReservationAsync(Guid recordId)
    {
        await using var migrator = new NpgsqlConnection(fixture.MigrationConnectionString);
        await migrator.OpenAsync();
        await using var cmd = new NpgsqlCommand("UPDATE core.sync_reservation SET expires_at = now() - interval '1 minute' WHERE record_id = @id;", migrator);
        cmd.Parameters.AddWithValue("id", recordId);
        await cmd.ExecuteNonQueryAsync();
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
