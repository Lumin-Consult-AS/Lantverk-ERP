using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Demo.Contacts;
using Erp.Client.Services;
using Erp.Core.Domain.Apps;
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
using Microsoft.Extensions.Hosting;
using Npgsql;
using Xunit;

namespace Erp.Core.Tests;

/// <summary>The app used to test migrations that stop half way: it moves data from one field to another in batches of two.</summary>
public sealed class LifecycleTestApp : AppBase
{
    /// <summary>The migration step (1-based) that throws once; 0 = never.</summary>
    public static int FailOnStep { get; set; }

    public static List<string> SeenCheckpoints { get; } = [];

    [Migration("Migrations.Move")]
    public async Task<MigrationStep> Move(Erp.Sdk.AppContext ctx, MigrationState state)
    {
        SeenCheckpoints.Add(state.Checkpoint ?? "<start>");
        var step = int.TryParse(state.Checkpoint, out var done) ? done + 1 : 1;
        if (step == FailOnStep)
        {
            FailOnStep = 0;
            throw new InvalidOperationException("boom: simulated crash half way through the migration");
        }

        var batch = await ctx.Data.QueryAsync("demo.lifecycle.item", "name isempty", top: 2);
        foreach (var item in batch)
        {
            await ctx.Data.UpdateAsync(
                "demo.lifecycle.item", item.Id, item.RowVersion,
                new Dictionary<string, object?> { ["name"] = "moved-" + item.Get<string>("legacyName") });
        }

        return batch.Count < 2 ? MigrationStep.Done(batch.Count) : MigrationStep.Continue(step.ToString(), batch.Count);
    }
}

[Collection(PostgresDatabaseCollection.Name)]
public sealed class AppLifecycleTests(PostgresDatabaseFixture fixture) : IAsyncLifetime
{
    private const string TestOidcSigningKey = "test-only-external-oidc-signing-key-at-least-32-bytes!";
    private const string TestOidcIssuer = "https://oidc.test.local/realms/erp-dev";
    private const string TestOidcAudience = "erp-web";

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

    // ------------------------------------------------------------------ the gate: 1.0 -> 1.1

    [Fact]
    public async Task Demo_contacts_UpgradesFrom1_0To1_1_RunsItsMigrationOnExistingData_AndTheSchemaHistoryCanBeQueried()
    {
        await using var factory = CreateFactory();
        await using var host = await StartContainerAsync<ContactsApp>(factory);
        RegisterContainer(factory, host, "demo.contacts", "1.1.0");
        var admin = await LoginAsync(factory, null);
        Assert.True(await admin.Api.InstallBuiltInAppAsync("core.parties"));

        try
        {
            // 1. Version 1.0.0 is installed and used: a party with three notes, none with a priority
            using var install10 = await PostPackageAsync(admin, ContactsPackage("1.0.0"));
            Assert.Equal(HttpStatusCode.OK, install10.StatusCode);
            Assert.Equal("installed", Root(await install10.Content.ReadAsStringAsync()).GetProperty("outcome").GetString());

            var party = await admin.Api.CreateRecordAsync("core.party", Party("P-LIFE-1"));
            for (var i = 1; i <= 3; i++)
            {
                await admin.Api.CreateRecordAsync("demo.contactnote", new Dictionary<string, object?>
                {
                    ["party"] = party.Id,
                    ["text"] = $"Notat {i}",
                    ["followUp"] = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(10).ToString("yyyy-MM-dd")
                });
            }

            var beforeUpgrade = DateTimeOffset.UtcNow;
            await Task.Delay(50);

            // 2. The same package again changes nothing
            using var again = await PostPackageAsync(admin, ContactsPackage("1.0.0"));
            var againText = await again.Content.ReadAsStringAsync();
            Assert.True(again.StatusCode == HttpStatusCode.OK, $"{(int)again.StatusCode}: {againText}");
            Assert.Equal("unchanged", Root(againText).GetProperty("outcome").GetString());

            // 3. Upgrade to 1.1.0: new optional field (minor, compatible) and a data migration that fills it in
            using var upgrade = await admin.Http.PostAsync("/api/v1/apps/install-builtin/demo.contacts", null);
            Assert.Equal(HttpStatusCode.OK, upgrade.StatusCode);
            var result = Root(await upgrade.Content.ReadAsStringAsync());
            Assert.Equal("upgraded", result.GetProperty("outcome").GetString());
            Assert.Equal("1.0.0", result.GetProperty("fromVersion").GetString());
            Assert.Equal("1.1.0", result.GetProperty("version").GetString());
            Assert.Contains(
                result.GetProperty("changes").EnumerateArray(),
                c => c.GetProperty("subject").GetString() == "demo.contactnote.priority" && !c.GetProperty("breaking").GetBoolean());
            Assert.DoesNotContain(result.GetProperty("changes").EnumerateArray(), c => c.GetProperty("breaking").GetBoolean());
            var run = Assert.Single(result.GetProperty("migrations").EnumerateArray());
            Assert.Equal("Migrations.V110", run.GetProperty("migrationClass").GetString());
            Assert.Equal("completed", run.GetProperty("status").GetString());
            Assert.Equal(3, run.GetProperty("recordsProcessed").GetInt32());

            // 4. The existing notes were migrated through the data API
            var notes = await admin.Api.QueryRecordsAsync("demo.contactnote", filter: $"party eq '{party.Id:D}'");
            Assert.Equal(3, notes.Count);
            Assert.All(notes, n => Assert.Equal("normal", I18nService.UnwrapDisplayString(n.Fields["priority"])));

            using var apps = Json(await admin.Http.GetStringAsync("/api/v1/apps"));
            Assert.Equal("1.1.0", apps.RootElement.EnumerateArray().Single(a => a.GetProperty("appId").GetString() == "demo.contacts").GetProperty("version").GetString());

            using var runs = Json(await admin.Http.GetStringAsync("/api/v1/apps/migrations?appId=demo.contacts"));
            Assert.Equal("completed", runs.RootElement.EnumerateArray().Single(r => r.GetProperty("migrationClass").GetString() == "Migrations.V110").GetProperty("status").GetString());

            // 5. Metadata snapshots answer "what did the schema look like then"
            using var snapshots = Json(await admin.Http.GetStringAsync("/api/v1/metadata/snapshots"));
            var upgradeSnapshot = snapshots.RootElement.EnumerateArray().Last(s => s.GetProperty("triggerAction").GetString() == "upgrade");
            var installSnapshot = snapshots.RootElement.EnumerateArray()
                .Last(s => s.GetProperty("triggerAction").GetString() == "install" && s.GetProperty("triggerAppId").GetString() == "demo.contacts");

            using var then = Json(await admin.Http.GetStringAsync(
                $"/api/v1/metadata/snapshots/at?at={Uri.EscapeDataString(beforeUpgrade.ToString("O"))}&objectId=demo.contactnote"));
            Assert.Equal("install", then.RootElement.GetProperty("triggerAction").GetString());
            Assert.DoesNotContain(FieldNames(then.RootElement.GetProperty("content")), n => n == "priority");

            using var now = Json(await admin.Http.GetStringAsync(
                $"/api/v1/metadata/snapshots/at?at={Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("O"))}&objectId=demo.contactnote"));
            Assert.Equal("upgrade", now.RootElement.GetProperty("triggerAction").GetString());
            Assert.Contains(FieldNames(now.RootElement.GetProperty("content")), n => n == "priority");

            using var diff = Json(await admin.Http.GetStringAsync(
                $"/api/v1/metadata/snapshots/diff?from={installSnapshot.GetProperty("snapshotNumber").GetInt32()}&to={upgradeSnapshot.GetProperty("snapshotNumber").GetInt32()}"));
            var changes = diff.RootElement.GetProperty("changes").EnumerateArray().Select(c => c.GetProperty("message").GetString()).ToList();
            Assert.Contains(changes, m => m!.Contains("priority"));
            Assert.Contains(changes, m => m!.Contains("1.0.0") && m.Contains("1.1.0"));

            using var specific = Json(await admin.Http.GetStringAsync($"/api/v1/metadata/snapshots/{installSnapshot.GetProperty("snapshotNumber").GetInt32()}"));
            Assert.Equal("install", specific.RootElement.GetProperty("triggerAction").GetString());
            using var missing = await admin.Http.GetAsync("/api/v1/metadata/snapshots/999999");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

            // 6. A downgrade is refused
            using var downgrade = await PostPackageAsync(admin, ContactsPackage("1.0.0"));
            Assert.Equal(HttpStatusCode.Conflict, downgrade.StatusCode);
            Assert.Contains("older", await downgrade.Content.ReadAsStringAsync());
        }
        finally
        {
            await admin.Api.UninstallAppAsync("demo.contacts");
            await admin.Api.UninstallAppAsync("core.parties");
        }
    }

    // ------------------------------------------------------------------ major upgrades, dependents and resuming

    [Fact]
    public async Task MajorUpgrade_NeedsAMigrationAndCompatibleDependents_AndAFailedMigrationResumesFromItsCheckpoint()
    {
        LifecycleTestApp.FailOnStep = 0;
        LifecycleTestApp.SeenCheckpoints.Clear();

        await using var factory = CreateFactory();
        await using var host = await StartContainerAsync<LifecycleTestApp>(factory);
        RegisterContainer(factory, host, "demo.lifecycle", "2.0.0");
        var admin = await LoginAsync(factory, null);

        try
        {
            using var install = await PostPackageAsync(admin, LifecyclePackage("1.0.0"));
            Assert.Equal(HttpStatusCode.OK, install.StatusCode);
            for (var i = 1; i <= 5; i++)
            {
                await admin.Api.CreateRecordAsync("demo.lifecycle.item", new Dictionary<string, object?> { ["legacyName"] = $"n{i}" });
            }

            // 1. A new major version that removes a field but has no migration is refused
            using var noMigration = await PostPackageAsync(admin, LifecyclePackage("2.0.0", withMigration: false));
            Assert.Equal(HttpStatusCode.Conflict, noMigration.StatusCode);
            Assert.Contains("requires a data migration", await noMigration.Content.ReadAsStringAsync());

            // 2. ...and a breaking change in a minor version is refused outright
            using var breakingMinor = await PostPackageAsync(admin, LifecyclePackage("1.1.0", withMigration: false, removeLegacy: true));
            Assert.Equal(HttpStatusCode.Conflict, breakingMinor.StatusCode);
            Assert.Contains("breaking", await breakingMinor.Content.ReadAsStringAsync());

            // 3. An installed app that does not support the new major blocks the upgrade
            using var dependent = await PostPackageAsync(admin, DependentPackage());
            Assert.Equal(HttpStatusCode.OK, dependent.StatusCode);
            using var blocked = await PostPackageAsync(admin, LifecyclePackage("2.0.0"));
            Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
            var blockedText = await blocked.Content.ReadAsStringAsync();
            Assert.Contains("demo.lifecycledep", blockedText);
            Assert.Contains("depends on", blockedText);

            using var removeDependent = await admin.Http.DeleteAsync("/api/v1/apps/demo.lifecycledep");
            Assert.Equal(HttpStatusCode.OK, removeDependent.StatusCode);

            // 4. The migration crashes after its first batch: the upgrade fails, but the progress is saved
            LifecycleTestApp.FailOnStep = 2;
            using var failed = await PostPackageAsync(admin, LifecyclePackage("2.0.0"));
            Assert.Equal(HttpStatusCode.Conflict, failed.StatusCode);
            var failure = Json(await failed.Content.ReadAsStringAsync());
            Assert.Contains("boom", failure.RootElement.GetProperty("error").GetString());
            var failedRun = failure.RootElement.GetProperty("migration");
            Assert.Equal("failed", failedRun.GetProperty("status").GetString());
            Assert.Equal("1", failedRun.GetProperty("checkpoint").GetString());
            Assert.Equal(2, failedRun.GetProperty("recordsProcessed").GetInt32());

            // The app is still at 1.0.0 and works; the metadata holds old and new fields together while the migration is open
            using var appsMid = Json(await admin.Http.GetStringAsync("/api/v1/apps"));
            Assert.Equal("1.0.0", appsMid.RootElement.EnumerateArray().Single(a => a.GetProperty("appId").GetString() == "demo.lifecycle").GetProperty("version").GetString());
            var midFields = await ObjectFieldNamesAsync(admin, "demo.lifecycle.item");
            Assert.Contains("legacyName", midFields);
            Assert.Contains("name", midFields);

            // 5. Running the upgrade again resumes from the checkpoint instead of starting over
            using var resumed = await PostPackageAsync(admin, LifecyclePackage("2.0.0"));
            Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
            var result = Json(await resumed.Content.ReadAsStringAsync());
            Assert.Equal("upgraded", result.RootElement.GetProperty("outcome").GetString());
            var run = Assert.Single(result.RootElement.GetProperty("migrations").EnumerateArray());
            Assert.Equal("completed", run.GetProperty("status").GetString());
            Assert.Equal(2, run.GetProperty("attempts").GetInt32());
            Assert.Equal(5, run.GetProperty("recordsProcessed").GetInt32());
            Assert.Equal(["<start>", "1", "1", "2"], LifecycleTestApp.SeenCheckpoints.ToArray());

            var items = await admin.Api.QueryRecordsAsync("demo.lifecycle.item", top: 50);
            Assert.Equal(5, items.Count);
            Assert.All(items, i => Assert.StartsWith("moved-n", I18nService.UnwrapDisplayString(i.Fields["name"])));

            // 6. Now the old field is retired: gone from the metadata, orphaned (not dropped) for the grace period
            var finalFields = await ObjectFieldNamesAsync(admin, "demo.lifecycle.item");
            Assert.DoesNotContain("legacyName", finalFields);
            using var objectJson = Json(await admin.Http.GetStringAsync("/api/v1/metadata/objects/demo.lifecycle.item"));
            var orphan = Assert.Single(objectJson.RootElement.GetProperty("orphanedColumns").EnumerateArray());
            Assert.Equal("demo.lifecycle", orphan.GetProperty("formerOwnerApp").GetString());
            Assert.InRange(
                orphan.GetProperty("dropAfter").GetDateTimeOffset(),
                DateTimeOffset.UtcNow.AddDays(29), DateTimeOffset.UtcNow.AddDays(31));
            Assert.True(await ColumnExistsAsync("demo", "lifecycle_item", orphan.GetProperty("columnName").GetString()!));
        }
        finally
        {
            await admin.Http.DeleteAsync("/api/v1/apps/demo.lifecycledep");
            using var cleanup = await admin.Http.DeleteAsync("/api/v1/apps/demo.lifecycle?purge=true");
        }
    }

    // ------------------------------------------------------------------ uninstall, archive and purge

    [Fact]
    public async Task Uninstall_ArchivesData_ReinstallRestoresIt_AndAfterTheGracePeriodNothingIsLeft()
    {
        await using var factory = CreateFactory();
        await using var host = await StartContainerAsync<ContactsApp>(factory);
        RegisterContainer(factory, host, "demo.contacts", "1.1.0");
        var admin = await LoginAsync(factory, null);
        Assert.True(await admin.Api.InstallBuiltInAppAsync("core.parties"));
        Assert.True(await admin.Api.InstallBuiltInAppAsync("demo.contacts"));

        try
        {
            var party = await admin.Api.CreateRecordAsync("core.party", PartyWithChannel("P-LIFE-2", "sms"));
            await admin.Api.CreateRecordAsync("demo.contactnote", new Dictionary<string, object?>
            {
                ["party"] = party.Id,
                ["text"] = "Skal arkiveres",
                ["followUp"] = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(5).ToString("yyyy-MM-dd")
            });

            // 1. Uninstalling archives the data for the grace period: it is hidden but not deleted
            using var uninstall = await admin.Http.DeleteAsync("/api/v1/apps/demo.contacts");
            Assert.Equal(HttpStatusCode.OK, uninstall.StatusCode);
            var archive = Json(await uninstall.Content.ReadAsStringAsync()).RootElement.GetProperty("archive");
            Assert.Equal("archived", archive.GetProperty("status").GetString());
            Assert.Contains("demo.contactnote", archive.GetProperty("objectIds").EnumerateArray().Select(o => o.GetString()));
            Assert.InRange(
                archive.GetProperty("purgeAfter").GetDateTimeOffset(),
                DateTimeOffset.UtcNow.AddDays(29), DateTimeOffset.UtcNow.AddDays(31));
            Assert.True(await RowCountAsync("demo", "contactnote") >= 1);

            // 2. Reinstalling within the period brings the data back
            Assert.True(await admin.Api.InstallBuiltInAppAsync("demo.contacts"));
            var restored = (await ArchivesAsync(admin)).Single(a => a.GetProperty("id").GetGuid() == archive.GetProperty("id").GetGuid());
            Assert.Equal("restored", restored.GetProperty("status").GetString());
            Assert.Single(await admin.Api.QueryRecordsAsync("demo.contactnote", filter: $"party eq '{party.Id:D}'"));

            // 3. Uninstall again; the grace period passes, and the retention worker removes the data for good
            using var second = await admin.Http.DeleteAsync("/api/v1/apps/demo.contacts");
            var secondArchiveId = Json(await second.Content.ReadAsStringAsync()).RootElement.GetProperty("archive").GetProperty("id").GetGuid();
            using var early = await admin.Http.PostAsync($"/api/v1/apps/archives/{secondArchiveId}/purge", null);
            Assert.Equal(HttpStatusCode.OK, early.StatusCode); // an administrator may purge before the period ends
            Assert.Equal("purged", Json(await early.Content.ReadAsStringAsync()).RootElement.GetProperty("status").GetString());

            Assert.False(await TableExistsAsync("demo", "contactnote"));
            using var purgedTwice = await admin.Http.PostAsync($"/api/v1/apps/archives/{secondArchiveId}/purge", null);
            Assert.Equal(HttpStatusCode.Conflict, purgedTwice.StatusCode);
        }
        finally
        {
            await admin.Api.UninstallAppAsync("demo.contacts");
            await admin.Api.UninstallAppAsync("core.parties");
        }
    }

    [Fact]
    public async Task TheRetentionWorker_PurgesExpiredArchives_AndDropsOrphanedColumnsOnlyWhenNoTenantUsesThem()
    {
        await using var factory = CreateFactory();
        await using var host = await StartContainerAsync<ContactsApp>(factory);
        RegisterContainer(factory, host, "demo.contacts", "1.1.0");
        var admin = await LoginAsync(factory, null);
        Assert.True(await admin.Api.InstallBuiltInAppAsync("core.parties"));
        Assert.True(await admin.Api.InstallBuiltInAppAsync("demo.contacts"));

        try
        {
            var party = await admin.Api.CreateRecordAsync("core.party", PartyWithChannel("P-LIFE-3", "email"));
            await admin.Api.CreateRecordAsync("demo.contactnote", new Dictionary<string, object?>
            {
                ["party"] = party.Id,
                ["text"] = "Utløper",
                ["followUp"] = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(5).ToString("yyyy-MM-dd")
            });

            using var uninstall = await admin.Http.DeleteAsync("/api/v1/apps/demo.contacts");
            Assert.Equal(HttpStatusCode.OK, uninstall.StatusCode);
            Assert.True(await ColumnExistsAsync("core", "party", "demo_contacts_preferred_channel"));
            var worker = factory.Services.GetServices<IHostedService>().OfType<AppRetentionWorker>().Single();

            // 1. Another tenant still has the extension field. The grace period of this tenant ends: its archived notes and
            //    its values in the column are deleted, but the column is shared and must stay for the other tenant.
            await SetOtherTenantPartyFieldAsync("active");
            try
            {
                await ExpireRetentionAsync();
                var first = await worker.RunOnceAsync(CancellationToken.None);
                Assert.True(first.ArchivesPurged >= 1);
                Assert.True(first.ColumnsCleared >= 1);
                Assert.Equal(0, first.ColumnsDropped);
                Assert.True(await ColumnExistsAsync("core", "party", "demo_contacts_preferred_channel"));
                Assert.Null(await ColumnValueAsync(party.Id));
                Assert.False(await TableExistsAsync("demo", "contactnote"));

                // 2. The other tenant removes the field too and its own grace period ends: now nobody uses the column
                await SetOtherTenantPartyFieldAsync("orphaned-expired");
                var second = await worker.RunOnceAsync(CancellationToken.None);
                Assert.True(second.ColumnsDropped >= 1);
                Assert.False(await ColumnExistsAsync("core", "party", "demo_contacts_preferred_channel"));
            }
            finally
            {
                await SetOtherTenantPartyFieldAsync("none");
            }

            // The rest of the party is untouched
            Assert.NotNull(await admin.Api.GetRecordAsync("core.party", party.Id));
        }
        finally
        {
            await admin.Api.UninstallAppAsync("core.parties");
        }
    }

    [Fact]
    public async Task PurgeNow_DeletesTheTenantsDataImmediately_AndAppManagementRequiresTenantAdmin()
    {
        await using var factory = CreateFactory();
        var admin = await LoginAsync(factory, null);
        var clerk = await LoginAsync(factory, await CreateUserAsync("saksbehandler", "demo.ikkeadmin"));

        using var forbiddenInstall = await clerk.Http.PostAsync("/api/v1/apps/install-builtin/demo.approvals", null);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenInstall.StatusCode);

        Assert.True(await admin.Api.InstallBuiltInAppAsync("demo.approvals"));
        try
        {
            await admin.Api.CreateRecordAsync("demo.expense", new Dictionary<string, object?> { ["title"] = "Slettes", ["amount"] = 10m });

            using var forbiddenUninstall = await clerk.Http.DeleteAsync("/api/v1/apps/demo.approvals");
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenUninstall.StatusCode);

            using var purge = await admin.Http.DeleteAsync("/api/v1/apps/demo.approvals?purge=true");
            Assert.Equal(HttpStatusCode.OK, purge.StatusCode);
            var archive = Json(await purge.Content.ReadAsStringAsync()).RootElement.GetProperty("archive");
            Assert.Equal("purged", archive.GetProperty("status").GetString());
            Assert.True(archive.GetProperty("rowsPurged").GetInt64() >= 1);
            Assert.False(await TableExistsAsync("demo", "expense"));
        }
        finally
        {
            await admin.Api.UninstallAppAsync("demo.approvals");
            await ResetWorkflowStateAsync();
        }
    }

    // ------------------------------------------------------------------ rules (no database)

    private static AppManifest Manifest(string yaml) => AppManifestParser.ParseManifestYaml(yaml, null);

    private const string BaseYaml =
        """
        id: demo.rules
        version: {V}
        name: Regler
        enums:
          demo.rules.status: [open, closed]
        objects:
          demo.rules.item:
            label: Element
            fields:
              name: { type: string, required: true }
              note: { type: string }
              amount: { type: decimal, scale: 2 }
        events:
          publishes:
            - demo.rules.item.created@1
        """;

    private static AppManifest Version(string version, string? replace = null, string? with = null, string extra = "")
    {
        var yaml = BaseYaml.Replace("{V}", version);
        if (replace is not null)
        {
            Assert.Contains(replace, yaml);
            yaml = yaml.Replace(replace, with ?? string.Empty);
        }

        return Manifest(yaml + extra);
    }

    private static List<string> Validate(AppManifest from, AppManifest to, params AppUpgradeValidator.InstalledDependent[] dependents) =>
        AppUpgradeValidator.Validate(from, to, AppUpgradeAnalyzer.Analyze(from, to), dependents);

    [Fact]
    public void UpgradeRules_AllowAdditiveMinorChanges_AndRefuseDowngradesAndBreakingMinorChanges()
    {
        var v1 = Version("1.0.0");

        // Additive: a new field, a new enum value, a new event
        var additive = Version(
            "1.1.0",
            "note: { type: string }",
            "note: { type: string }\n      comment: { type: text }",
            extra: "");
        Assert.Empty(Validate(v1, additive));
        Assert.All(AppUpgradeAnalyzer.Analyze(v1, additive), c => Assert.False(c.Breaking));

        var withEnumValue = Version("1.1.0", "[open, closed]", "[open, closed, archived]");
        Assert.Empty(Validate(v1, withEnumValue));

        // Not newer
        Assert.Contains("already installed", Assert.Single(Validate(v1, Version("1.0.0"))));
        Assert.Contains("downgrades", Assert.Single(Validate(Version("1.2.0"), Version("1.1.0"))));

        // Breaking changes are not allowed within a major version
        foreach (var (label, broken) in new[]
                 {
                     ("a removed field", Version("1.1.0", "      note: { type: string }\n", string.Empty)),
                     ("a changed type", Version("1.1.0", "amount: { type: decimal, scale: 2 }", "amount: { type: string }")),
                     ("a narrower scale", Version("1.1.0", "scale: 2", "scale: 1")),
                     ("a removed enum value", Version("1.1.0", "[open, closed]", "[open]")),
                     ("a removed event", Version("1.1.0", "    - demo.rules.item.created@1", string.Empty)),
                     ("a new required field without default", Version("1.1.0", "note: { type: string }", "note: { type: string }\n      code: { type: string, required: true }"))
                 })
        {
            var errors = Validate(v1, broken);
            Assert.True(errors.Count > 0, $"{label} must be refused in a minor version");
            Assert.Contains("breaking", errors[0]);
        }

        // A required field with a default is fine
        Assert.Empty(Validate(v1, Version("1.1.0", "note: { type: string }", "note: { type: string }\n      code: { type: string, required: true, default: x }")));
    }

    [Fact]
    public void UpgradeRules_MajorVersionsNeedAMigration_AndDependentsMustSupportThem()
    {
        var v1 = Version("1.0.0");
        const string migration = "\nmigrations:\n  - from: 1.0.0\n    to: 2.0.0\n    run: Migrations.V200\n";
        var breakingWithoutMigration = Version("2.0.0", "      note: { type: string }\n", string.Empty);
        var breakingWithMigration = Version("2.0.0", "      note: { type: string }\n", string.Empty, migration);

        Assert.Contains("requires a data migration", Assert.Single(Validate(v1, breakingWithoutMigration)));
        Assert.Empty(Validate(v1, breakingWithMigration));

        // A migration must exist for a major bump even without breaking changes (the version says data may change)
        Assert.Contains("requires a data migration", Assert.Single(Validate(v1, Version("2.0.0"))));

        // Dependents
        var narrowDependent = new AppUpgradeValidator.InstalledDependent(
            "demo.other", "1.0.0", Manifest("id: demo.other\nversion: 1.0.0\nname: Annen\ndependencies:\n  demo.rules: \">=1.0.0 <2.0.0\"\n"));
        var wideDependent = new AppUpgradeValidator.InstalledDependent(
            "demo.wide", "1.0.0", Manifest("id: demo.wide\nversion: 1.0.0\nname: Bred\ndependencies:\n  demo.rules: \">=1.0.0 <3.0.0\"\n"));

        var blocked = Validate(v1, breakingWithMigration, narrowDependent, wideDependent);
        var message = Assert.Single(blocked);
        Assert.Contains("demo.other", message);
        Assert.DoesNotContain("demo.wide", message);

        // A minor upgrade never needs the dependents to change
        Assert.Empty(Validate(v1, Version("1.1.0", "note: { type: string }", "note: { type: string }\n      comment: { type: text }"), narrowDependent));
    }

    [Fact]
    public void UpgradeRules_ValidateTheMigrationDeclarations_AndPickTheMigrationsBetweenTwoVersions()
    {
        var v1 = Version("1.0.0");
        var tooNew = Version("1.1.0", extra: "\nmigrations:\n  - from: 1.0.0\n    to: 1.5.0\n    run: Migrations.Later\n");
        Assert.Contains(Validate(v1, tooNew), e => e.Contains("newer than the app's version"));

        var backwards = Version("1.1.0", extra: "\nmigrations:\n  - from: 1.1.0\n    to: 1.0.0\n    run: Migrations.Back\n");
        Assert.Contains(Validate(v1, backwards), e => e.Contains("higher version"));

        var chain = Version(
            "3.0.0",
            extra:
            """

            migrations:
              - from: 0.9.0
                to: 1.0.0
                run: Migrations.V100
              - from: 1.0.0
                to: 2.0.0
                run: Migrations.V200
              - from: 2.0.0
                to: 3.0.0
                run: Migrations.V300
            """);

        Assert.Equal(["Migrations.V200", "Migrations.V300"], MigrationPlanner.Select(chain, "1.0.0").Select(m => m.Run).ToArray());
        Assert.Equal(["Migrations.V300"], MigrationPlanner.Select(chain, "2.0.0").Select(m => m.Run).ToArray());
        Assert.Equal(["Migrations.V100", "Migrations.V200", "Migrations.V300"], MigrationPlanner.Select(chain, "0.9.0").Select(m => m.Run).ToArray());
        Assert.Empty(MigrationPlanner.Select(chain, "3.0.0"));
    }

    // ------------------------------------------------------------------ packages

    private static string ResolveSampleDirectory(string appId)
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "erp-samples", appId);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException($"Sample app '{appId}' was not found.");
    }

    /// <summary>
    /// The demo.contacts sample is the 1.1.0 package. Version 1.0.0 is that package without what 1.1.0 added
    /// (the priority field and enum, the migration, and their page columns), so one source serves both versions.
    /// </summary>
    private static byte[] ContactsPackage(string version)
    {
        var root = ResolveSampleDirectory("demo.contacts");
        var files = new Dictionary<string, string>();
        foreach (var path in Directory.EnumerateFiles(root, "*.yaml", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (relative.StartsWith("bin/", StringComparison.Ordinal) || relative.StartsWith("obj/", StringComparison.Ordinal))
            {
                continue;
            }

            files[relative] = File.ReadAllText(path);
        }

        if (version == "1.0.0")
        {
            var yaml = files["app.yaml"].Replace("\r\n", "\n");
            yaml = yaml.Replace("version: 1.1.0", "version: 1.0.0")
                .Replace("demo-contacts:1.1.0", "demo-contacts:1.0.0")
                .Replace("  demo.priority: [low, normal, high]\n", string.Empty)
                .Replace("      priority:\n        type: enum\n        of: demo.priority\n        label: contacts.fields.priority\n", string.Empty);
            yaml = Regex.Replace(yaml, @"\n# 1\.1\.0 adds.*", "\n", RegexOptions.Singleline);
            files["app.yaml"] = yaml;

            foreach (var page in new[] { "pages/contactnote-list.yaml", "pages/contactnote-card.yaml" })
            {
                files[page] = files[page].Replace("\r\n", "\n").Replace("  - priority\n", string.Empty).Replace("      - priority\n", string.Empty);
            }
        }

        return AppPackageFormat.CreateSignedZipFromFiles(files);
    }

    private static byte[] LifecyclePackage(string version, bool withMigration = true, bool removeLegacy = false)
    {
        var major = version.StartsWith('2');
        var fields = major || removeLegacy
            ? "        name: { type: string }"
            : "        legacyName: { type: string }";
        var yaml = $"""
            id: demo.lifecycle
            version: {version}
            name: Livssyklus
            runtime:
              image: ghcr.io/example/demo-lifecycle:{version}
            objects:
              demo.lifecycle.item:
                label: Element
                fields:
            {fields}
            """;
        if (major && !removeLegacy)
        {
            // 2.0.0 keeps "name" and drops "legacyName": the migration moves the data over
        }

        if (withMigration && major)
        {
            yaml += "\nmigrations:\n  - from: 1.0.0\n    to: 2.0.0\n    run: Migrations.Move\n";
        }

        return AppPackageFormat.CreateSignedZipFromFiles(new Dictionary<string, string> { ["app.yaml"] = yaml });
    }

    private static byte[] DependentPackage() => AppPackageFormat.CreateSignedZipFromFiles(new Dictionary<string, string>
    {
        ["app.yaml"] =
            """
            id: demo.lifecycledep
            version: 1.0.0
            name: Avhengig
            dependencies:
              demo.lifecycle: ">=1.0.0 <2.0.0"
            objects:
              demo.lifecycledep.tag:
                label: Tagg
                fields:
                  name: { type: string }
            """
    });

    // ------------------------------------------------------------------ helpers

    private sealed class Session(HttpClient http, ErpApiClient api)
    {
        public HttpClient Http { get; } = http;
        public ErpApiClient Api { get; } = api;
    }

    private static Dictionary<string, object?> Party(string number) => new()
    {
        ["partyNumber"] = number,
        ["name"] = "Livssyklus AS",
        ["partyType"] = "organization",
        ["role"] = "customer",
        ["organizationNumber"] = "955333444"
    };

    private static Dictionary<string, object?> PartyWithChannel(string number, string channel)
    {
        var party = Party(number);
        party["demo.contacts.preferredChannel"] = channel;
        return party;
    }

    private static JsonDocument Json(string text) => JsonDocument.Parse(text);

    private static JsonElement Root(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private static IEnumerable<string> FieldNames(JsonElement objectJson) =>
        objectJson.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Array
            ? fields.EnumerateArray().Select(f => f.GetProperty("name").GetString()!).ToList()
            : [];

    private static async Task<List<string>> ObjectFieldNamesAsync(Session session, string objectId)
    {
        using var doc = Json(await session.Http.GetStringAsync($"/api/v1/metadata/objects/{objectId}"));
        return FieldNames(doc.RootElement).ToList();
    }

    private static async Task<List<JsonElement>> ArchivesAsync(Session session)
    {
        using var doc = Json(await session.Http.GetStringAsync("/api/v1/apps/archives"));
        return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private static Task<HttpResponseMessage> PostPackageAsync(Session session, byte[] zip)
    {
        var content = new ByteArrayContent(zip);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        return session.Http.PostAsync("/api/v1/apps/install", content);
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

    private static void RegisterContainer(WebApplicationFactory<Program> core, WebApplication host, string appId, string version) =>
        core.Services.GetRequiredService<KubernetesAppOrchestrator>().RegisterEndpoint(
            appId, version, $"http://{appId.Replace('.', '-')}:50051", () => host.GetTestServer().CreateHandler());

    private async Task<bool> TableExistsAsync(string schema, string table)
    {
        await using var migrator = new NpgsqlConnection(fixture.MigrationConnectionString);
        await migrator.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT to_regclass(@name) IS NOT NULL;", migrator);
        cmd.Parameters.AddWithValue("name", $"{schema}.{table}");
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<bool> ColumnExistsAsync(string schema, string table, string column)
    {
        await using var migrator = new NpgsqlConnection(fixture.MigrationConnectionString);
        await migrator.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = @s AND table_name = @t AND column_name = @c);", migrator);
        cmd.Parameters.AddWithValue("s", schema);
        cmd.Parameters.AddWithValue("t", table);
        cmd.Parameters.AddWithValue("c", column);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<long> RowCountAsync(string schema, string table)
    {
        await using var migrator = new NpgsqlConnection(fixture.MigrationConnectionString);
        await migrator.OpenAsync();
        await using var cmd = new NpgsqlCommand($"""SELECT count(*) FROM "{schema}"."{table}" WHERE tenant_id = @t;""", migrator);
        cmd.Parameters.AddWithValue("t", DemoTenantSeeder.DemoTenantId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<object?> ColumnValueAsync(Guid partyId)
    {
        await using var migrator = new NpgsqlConnection(fixture.MigrationConnectionString);
        await migrator.OpenAsync();
        await using var cmd = new NpgsqlCommand("""SELECT demo_contacts_preferred_channel FROM core.party WHERE id = @id;""", migrator);
        cmd.Parameters.AddWithValue("id", partyId);
        var value = await cmd.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    /// <summary>Moves the retention dates of this tenant's archives and orphaned columns into the past.</summary>
    private async Task ExpireRetentionAsync()
    {
        await using var migrator = new NpgsqlConnection(fixture.MigrationConnectionString);
        await migrator.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            UPDATE core.app_archive SET purge_after = now() - interval '1 day' WHERE status = 'archived' AND tenant_id = @t;
            UPDATE core.metadata_item
               SET definition_json = jsonb_set(
                       definition_json, '{orphanedColumns}',
                       (SELECT COALESCE(jsonb_agg(jsonb_set(o, '{dropAfter}', to_jsonb(to_char(now() - interval '1 day', 'YYYY-MM-DD"T"HH24:MI:SS"Z"')))), '[]'::jsonb)
                          FROM jsonb_array_elements(definition_json -> 'orphanedColumns') o))
             WHERE kind = 'object' AND tenant_id = @t AND jsonb_array_length(COALESCE(definition_json -> 'orphanedColumns', '[]'::jsonb)) > 0;
            """,
            migrator);
        cmd.Parameters.AddWithValue("t", DemoTenantSeeder.DemoTenantId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// The preferred-channel column on core.party is shared by all tenants. This sets what the second demo tenant's
    /// metadata says about it: <c>active</c> (it still has the field), <c>orphaned-expired</c> (it removed the field and
    /// its grace period has ended) or <c>none</c>.
    /// </summary>
    private async Task SetOtherTenantPartyFieldAsync(string mode)
    {
        await using var migrator = new NpgsqlConnection(fixture.MigrationConnectionString);
        await migrator.OpenAsync();

        if (mode == "none")
        {
            await using var delete = new NpgsqlCommand(
                "DELETE FROM core.metadata_item WHERE tenant_id = @t AND kind = 'object' AND item_id = 'core.party';", migrator);
            delete.Parameters.AddWithValue("t", DemoTenantSeeder.SecondTenantId);
            await delete.ExecuteNonQueryAsync();
            return;
        }

        var definition = mode == "active"
            ? """{"id":"core.party","ownerApp":"core.parties","fields":[{"name":"preferredChannel","ownerApp":"demo.contacts","type":"string"}],"orphanedColumns":[]}"""
            : """{"id":"core.party","ownerApp":"core.parties","fields":[],"orphanedColumns":[{"columnName":"demo_contacts_preferred_channel","fieldName":"preferredChannel","formerOwnerApp":"demo.contacts","type":"string","orphanedAt":"2026-01-01T00:00:00Z","dropAfter":"2026-01-31T00:00:00Z"}]}""";

        await using var upsert = new NpgsqlCommand(
            """
            INSERT INTO core.metadata_item
                (id, tenant_id, created_at, created_by, updated_at, updated_by, row_version, kind, item_id, owner_app, version, definition_json)
            VALUES (gen_random_uuid(), @t, now(), @t, now(), @t, 1, 'object', 'core.party', 'core.parties', '1.0.0', @def::jsonb)
            ON CONFLICT (tenant_id, kind, item_id) DO UPDATE SET definition_json = EXCLUDED.definition_json;
            """,
            migrator);
        upsert.Parameters.AddWithValue("t", DemoTenantSeeder.SecondTenantId);
        upsert.Parameters.AddWithValue("def", definition);
        await upsert.ExecuteNonQueryAsync();
    }

    private async Task ResetWorkflowStateAsync()
    {
        await using var migrator = new NpgsqlConnection(fixture.MigrationConnectionString);
        await migrator.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE core.task SET status = 'cancelled' WHERE status IN ('open', 'waiting'); " +
            "UPDATE core.workflow_instance SET status = 'rejected', completed_at = now() WHERE status = 'running';",
            migrator);
        await cmd.ExecuteNonQueryAsync();
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
