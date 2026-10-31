using System.Text.Json;
using System.Text.Json.Serialization;
using Erp.Core.Domain.Apps;
using Erp.Core.Domain.Entities;
using Erp.Core.Domain.Metadata;
using Erp.Core.Domain.Tenancy;
using Erp.Core.Infrastructure.Metadata;
using Erp.Core.Infrastructure.Persistence;
using Erp.Core.Infrastructure.Workflow;
using Microsoft.EntityFrameworkCore;

namespace Erp.Core.Infrastructure.Apps;

/// <summary>
/// Manages application package installation, upgrade, uninstallation, and point-in-time
/// tenant metadata snapshots (<c>core.metadata_snapshot</c>).
/// </summary>
public sealed class AppInstallationService(
    CoreDbContext dbContext,
    MetadataRegistryService metadataRegistry,
    IAppPackageStorage packageStorage,
    ITenantContext tenantContext,
    KubernetesAppOrchestrator? appOrchestrator = null,
    WorkflowStore? workflowStore = null,
    AppMigrationService? migrations = null,
    AppRetentionService? retention = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        WriteIndented = false
    };

    public async Task<AppInstallResult> InstallPackageAsync(
        byte[] zipBytes,
        string? signingKey = null,
        CancellationToken cancellationToken = default)
    {
        var tenantId = tenantContext.TenantId
            ?? throw new InvalidOperationException("Cannot install an app without an active tenant context.");

        // 1. Verify cryptographic signature and parse manifest + auxiliary files
        var verified = AppPackageFormat.ReadAndVerifyPackage(
            zipBytes,
            signingKey ?? AppPackageFormat.DefaultDevSigningKey);
        var manifest = verified.Manifest;

        // 2. Load current tenant state for installation rule validation
        var installedApps = await dbContext.InstalledApps
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var existingApp = installedApps.FirstOrDefault(a => string.Equals(a.AppId, manifest.Id, StringComparison.OrdinalIgnoreCase));

        // The same package again changes nothing. Compared by content: a rebuilt zip of identical files has another hash.
        if (existingApp is not null && SameManifest(existingApp.ManifestJson, manifest))
        {
            return new AppInstallResult(existingApp, "unchanged", existingApp.Version, [], [], null);
        }

        AppManifest? installedManifest = null;
        IReadOnlyList<SchemaChange> changes = [];
        if (existingApp is not null)
        {
            // 2b. Upgrade rules (ADR-0013): newer version, breaking changes only in a new major (with a migration),
            //     and dependent apps must support a new major.
            installedManifest = JsonSerializer.Deserialize<AppManifest>(existingApp.ManifestJson, JsonOptions)
                ?? throw new InvalidOperationException($"The stored manifest of '{existingApp.AppId}' is unreadable.");
            changes = AppUpgradeAnalyzer.Analyze(installedManifest, manifest);

            var dependents = installedApps
                .Where(a => !string.Equals(a.AppId, manifest.Id, StringComparison.OrdinalIgnoreCase))
                .Select(a => new AppUpgradeValidator.InstalledDependent(
                    a.AppId, a.Version, JsonSerializer.Deserialize<AppManifest>(a.ManifestJson, JsonOptions)!))
                .ToList();

            var upgradeErrors = AppUpgradeValidator.Validate(installedManifest, manifest, changes, dependents);
            if (upgradeErrors.Count > 0)
            {
                throw new AppUpgradeException(upgradeErrors);
            }
        }

        var installedVersions = installedApps
            .Where(a => !string.Equals(a.AppId, manifest.Id, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(a => a.AppId, a => a.Version, StringComparer.OrdinalIgnoreCase);

        var existingObjectsList = await metadataRegistry.GetObjectsAsync(cancellationToken);
        var existingObjects = existingObjectsList.ToDictionary(o => o.Id, StringComparer.OrdinalIgnoreCase);

        var existingEventItems = await dbContext.MetadataItems
            .AsNoTracking()
            .Where(m => m.Kind == "event")
            .ToListAsync(cancellationToken);
        var existingEvents = existingEventItems
            .Select(e => e.ItemId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 3. Enforce all installation rules
        AppManifestValidator.ValidateForInstallation(
            manifest,
            installedVersions,
            existingObjects,
            existingEvents);

        // 4. Store signed package in object storage
        var storageKey = await packageStorage.StorePackageAsync(
            tenantId,
            manifest.Id,
            manifest.Version,
            verified.PackageSha256,
            verified.ZipBytes,
            cancellationToken);

        var isUpgrade = existingApp is not null && installedManifest is not null;

        // 5. Provision schemas, tables, columns, indexes, RLS, and audit triggers. During an upgrade the metadata first
        //    holds old and new together, so a data migration can read what is being removed while it writes what is new.
        var (union, differs) = isUpgrade
            ? AppMetadataUnion.Build(installedManifest!.Metadata, manifest.Metadata, JsonOptions)
            : (manifest.Metadata, false);
        await metadataRegistry.ApplyBundleAsync(union, cancellationToken);

        // 5b. If the app defines a container runtime, provision/ensure shared Deployment and NetworkPolicy
        if (!manifest.IsDeclarative && manifest.Runtime is not null)
        {
            appOrchestrator?.EnsureContainerRunning(manifest);
        }

        // 5c. Data migrations, per tenant, with a progress log. The installed version stays the old one until they have
        //     completed, so a failed upgrade can simply be run again and resumes from its last checkpoint.
        IReadOnlyList<MigrationRunInfo> migrationRuns = [];
        AppArchiveInfo? upgradeArchive = null;
        if (isUpgrade)
        {
            if (migrations is not null)
            {
                migrationRuns = await migrations.RunAsync(manifest, existingApp!.Version, cancellationToken);
            }

            // Now what the new version no longer declares can go: fields become orphaned columns, removed objects are
            // archived. Nothing is deleted from the database here.
            var dropAfter = retention?.PurgeDate ?? DateTimeOffset.UtcNow.AddDays(30);
            var removedObjects = await metadataRegistry.ReconcileOwnedAsync(manifest.Id, manifest.Metadata, dropAfter, cancellationToken);
            if (differs)
            {
                await metadataRegistry.ApplyBundleAsync(manifest.Metadata, cancellationToken);
            }

            if (retention is not null)
            {
                upgradeArchive = await retention.ArchiveAsync(manifest.Id, existingApp!.Version, removedObjects, "upgrade", cancellationToken);
            }
        }
        else if (retention is not null)
        {
            // Reinstalling within the grace period brings the archived data back.
            await retention.RestoreAsync(manifest.Id, cancellationToken);
        }

        // 6. Upsert InstalledApp record
        var trackedApp = await dbContext.InstalledApps
            .FirstOrDefaultAsync(a => a.AppId == manifest.Id, cancellationToken);

        InstalledApp appRecord;
        if (trackedApp is null)
        {
            appRecord = new InstalledApp
            {
                Id = Guid.NewGuid(),
                AppId = manifest.Id,
                Version = manifest.Version,
                Name = manifest.Name,
                Publisher = manifest.Publisher,
                IsDeclarative = manifest.IsDeclarative,
                ContainerImage = manifest.Runtime?.Image,
                PackageSha256 = verified.PackageSha256,
                PackageStorageKey = storageKey,
                ManifestJson = JsonSerializer.Serialize(manifest, JsonOptions),
                PagesJson = JsonSerializer.Serialize(manifest.Pages, JsonOptions),
                PermissionSetsJson = JsonSerializer.Serialize(manifest.PermissionSets, JsonOptions),
                TranslationsJson = JsonSerializer.Serialize(manifest.Translations, JsonOptions)
            };
            dbContext.InstalledApps.Add(appRecord);
        }
        else
        {
            trackedApp.Version = manifest.Version;
            trackedApp.Name = manifest.Name;
            trackedApp.Publisher = manifest.Publisher;
            trackedApp.IsDeclarative = manifest.IsDeclarative;
            trackedApp.ContainerImage = manifest.Runtime?.Image;
            trackedApp.PackageSha256 = verified.PackageSha256;
            trackedApp.PackageStorageKey = storageKey;
            trackedApp.ManifestJson = JsonSerializer.Serialize(manifest, JsonOptions);
            trackedApp.PagesJson = JsonSerializer.Serialize(manifest.Pages, JsonOptions);
            trackedApp.PermissionSetsJson = JsonSerializer.Serialize(manifest.PermissionSets, JsonOptions);
            trackedApp.TranslationsJson = JsonSerializer.Serialize(manifest.Translations, JsonOptions);
            appRecord = trackedApp;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        // 6b. Copy the app's default workflows into the tenant's own workflow definitions
        if (workflowStore is not null)
        {
            await workflowStore.SeedFromManifestAsync(manifest, cancellationToken);
        }

        // 7. Capture immutable metadata snapshot for the tenant
        await CaptureMetadataSnapshotAsync(
            triggerAction: isUpgrade ? "upgrade" : "install",
            triggerAppId: manifest.Id,
            triggerAppVersion: manifest.Version,
            cancellationToken);

        return new AppInstallResult(
            appRecord,
            isUpgrade ? "upgraded" : "installed",
            existingApp?.Version,
            changes,
            migrationRuns,
            upgradeArchive);
    }

    /// <summary>The stored manifest is a jsonb column, which does not keep key order or spacing; compare the content.</summary>
    private static bool SameManifest(string storedJson, AppManifest manifest) =>
        System.Text.Json.Nodes.JsonNode.DeepEquals(
            System.Text.Json.Nodes.JsonNode.Parse(storedJson),
            System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(manifest, JsonOptions)));

    /// <summary>
    /// Uninstalls an app. Its objects' data is archived, not deleted: it is kept for the grace period and restored if the
    /// app is installed again. <paramref name="purgeNow"/> deletes the tenant's data at once instead.
    /// </summary>
    public async Task<AppUninstallResult> UninstallAppAsync(
        string appId,
        bool purgeNow = false,
        CancellationToken cancellationToken = default)
    {
        var installedApp = await dbContext.InstalledApps
            .FirstOrDefaultAsync(a => a.AppId == appId, cancellationToken)
            ?? throw new KeyNotFoundException($"App '{appId}' is not installed in this tenant.");

        // Verify no other installed app in the tenant depends on this app
        var otherApps = await dbContext.InstalledApps
            .AsNoTracking()
            .Where(a => a.AppId != appId)
            .ToListAsync(cancellationToken);

        foreach (var other in otherApps)
        {
            var otherManifest = JsonSerializer.Deserialize<AppManifest>(other.ManifestJson, JsonOptions);
            if (otherManifest?.Dependencies is not null
                && otherManifest.Dependencies.ContainsKey(appId))
            {
                throw new AppDependencyException(
                    $"Cannot uninstall app '{appId}' because installed app '{other.AppId}' ({other.Version}) depends on it.");
            }
        }

        // Remove metadata items owned by this app (the physical tables and columns remain; the data is archived below)
        var ownedItems = await dbContext.MetadataItems
            .Where(m => m.OwnerApp == appId)
            .ToListAsync(cancellationToken);
        var ownedObjectIds = ownedItems.Where(m => m.Kind == "object").Select(m => m.ItemId).ToList();
        dbContext.MetadataItems.RemoveRange(ownedItems);

        // Also remove any extension fields owned by this app on other apps' objects
        var otherObjectItems = await dbContext.MetadataItems
            .Where(m => m.Kind == "object" && m.OwnerApp != appId)
            .ToListAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var dropAfter = retention?.PurgeDate ?? now.AddDays(30);
        foreach (var objItem in otherObjectItems)
        {
            var objDef = JsonSerializer.Deserialize<ObjectDefinition>(objItem.DefinitionJson, JsonOptions);
            if (objDef is not null)
            {
                var removedFields = objDef.Fields
                    .Where(f => string.Equals(f.OwnerApp, appId, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (removedFields.Count > 0)
                {
                    foreach (var rf in removedFields)
                    {
                        var colName = rf.GetColumnName(objDef.OwnerApp);
                        objDef.OrphanedColumns.RemoveAll(o =>
                            string.Equals(o.ColumnName, colName, StringComparison.OrdinalIgnoreCase));

                        objDef.OrphanedColumns.Add(new OrphanedColumnDefinition
                        {
                            ColumnName = colName,
                            FieldName = rf.Name,
                            FormerOwnerApp = appId,
                            Type = rf.Type,
                            OrphanedAt = now,
                            DropAfter = dropAfter
                        });
                    }

                    objDef.Fields.RemoveAll(
                        f => string.Equals(f.OwnerApp, appId, StringComparison.OrdinalIgnoreCase));
                    objItem.DefinitionJson = JsonSerializer.Serialize(objDef, JsonOptions);
                }
            }
        }

        var uninstalledVersion = installedApp.Version;
        dbContext.InstalledApps.Remove(installedApp);
        await dbContext.SaveChangesAsync(cancellationToken);

        if (workflowStore is not null)
        {
            await workflowStore.RemoveAppDefaultsAsync(appId, cancellationToken);
        }

        AppArchiveInfo? archive = null;
        if (retention is not null)
        {
            archive = await retention.ArchiveAsync(appId, uninstalledVersion, ownedObjectIds, "uninstall", cancellationToken);
            if (archive is not null && purgeNow)
            {
                archive = await retention.PurgeAsync(archive.Id, cancellationToken);
            }
        }

        // Capture immutable metadata snapshot after uninstallation
        var snapshot = await CaptureMetadataSnapshotAsync(
            triggerAction: "uninstall",
            triggerAppId: appId,
            triggerAppVersion: uninstalledVersion,
            cancellationToken);
        return new AppUninstallResult(snapshot, archive);
    }

    public async Task<IReadOnlyList<InstalledApp>> GetInstalledAppsAsync(CancellationToken cancellationToken = default)
        => await dbContext.InstalledApps
            .AsNoTracking()
            .OrderBy(a => a.AppId)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<PageDefinition>> GetEffectivePagesAsync(CancellationToken cancellationToken = default)
    {
        var apps = await dbContext.InstalledApps
            .AsNoTracking()
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(cancellationToken);

        var pagesById = new Dictionary<string, PageDefinition>(StringComparer.OrdinalIgnoreCase);
        var extensions = new List<PageExtensionDefinition>();

        foreach (var app in apps)
        {
            var manifest = JsonSerializer.Deserialize<AppManifest>(app.ManifestJson, JsonOptions);
            if (manifest is null) continue;

            foreach (var page in manifest.Pages)
            {
                pagesById[page.Id] = page;
            }

            extensions.AddRange(manifest.PageExtensions);
        }

        // Merge page extensions (tabs and field placements) into target pages
        foreach (var ext in extensions)
        {
            if (!pagesById.TryGetValue(ext.Extend, out var targetPage))
            {
                continue;
            }

            foreach (var tab in ext.Tabs)
            {
                targetPage.Tabs.Add(tab);
            }

            foreach (var fieldPlacement in ext.Fields)
            {
                PlaceExtendedFieldOnPage(targetPage, fieldPlacement);
            }
        }

        return pagesById.Values.OrderBy(p => p.Id).ToList();
    }

    public async Task<IReadOnlyList<PermissionSetDefinition>> GetPermissionSetsAsync(CancellationToken cancellationToken = default)
    {
        var apps = await dbContext.InstalledApps
            .AsNoTracking()
            .OrderBy(a => a.AppId)
            .ToListAsync(cancellationToken);

        var result = new List<PermissionSetDefinition>();
        foreach (var app in apps)
        {
            var perms = JsonSerializer.Deserialize<List<PermissionSetDefinition>>(app.PermissionSetsJson, JsonOptions);
            if (perms is not null)
            {
                result.AddRange(perms);
            }
        }

        return result;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetTranslationsAsync(
        string locale,
        CancellationToken cancellationToken = default)
    {
        var apps = await dbContext.InstalledApps
            .AsNoTracking()
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(cancellationToken);

        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in apps)
        {
            var allLocales = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(
                app.TranslationsJson,
                JsonOptions);
            if (allLocales is null) continue;

            // Fallback to 'nb' first, then overlay requested locale
            if (allLocales.TryGetValue("nb", out var nbMap))
            {
                foreach (var (k, v) in nbMap) merged[k] = v;
            }

            if (!string.Equals(locale, "nb", StringComparison.OrdinalIgnoreCase)
                && allLocales.TryGetValue(locale, out var reqMap))
            {
                foreach (var (k, v) in reqMap) merged[k] = v;
            }
        }

        return merged;
    }

    public async Task<IReadOnlyList<MetadataSnapshot>> GetSnapshotsAsync(CancellationToken cancellationToken = default)
        => await dbContext.MetadataSnapshots
            .AsNoTracking()
            .OrderBy(s => s.SnapshotNumber)
            .ToListAsync(cancellationToken);

    public async Task<MetadataSnapshot?> GetSnapshotAsync(int snapshotNumber, CancellationToken cancellationToken = default)
        => await dbContext.MetadataSnapshots
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.SnapshotNumber == snapshotNumber, cancellationToken);

    /// <summary>The snapshot that applied at the given time: the newest one captured at or before it ("what did the schema look like then").</summary>
    public async Task<MetadataSnapshot?> GetSnapshotAtAsync(DateTimeOffset at, CancellationToken cancellationToken = default)
        => await dbContext.MetadataSnapshots
            .AsNoTracking()
            .Where(s => s.CreatedAt <= at)
            .OrderByDescending(s => s.SnapshotNumber)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<SchemaChange>> DiffSnapshotsAsync(int fromNumber, int toNumber, CancellationToken cancellationToken = default)
    {
        var from = await GetSnapshotAsync(fromNumber, cancellationToken)
            ?? throw new KeyNotFoundException($"Snapshot {fromNumber} was not found.");
        var to = await GetSnapshotAsync(toNumber, cancellationToken)
            ?? throw new KeyNotFoundException($"Snapshot {toNumber} was not found.");

        return SnapshotDiff.Compute(
            JsonSerializer.Deserialize<TenantMetadataSnapshotPayload>(from.SnapshotJson, JsonOptions)!,
            JsonSerializer.Deserialize<TenantMetadataSnapshotPayload>(to.SnapshotJson, JsonOptions)!);
    }

    private async Task<MetadataSnapshot> CaptureMetadataSnapshotAsync(
        string triggerAction,
        string triggerAppId,
        string triggerAppVersion,
        CancellationToken cancellationToken)
    {
        var maxSnapshotNumber = await dbContext.MetadataSnapshots
            .AsNoTracking()
            .Select(s => (int?)s.SnapshotNumber)
            .MaxAsync(cancellationToken) ?? 0;

        var nextNumber = maxSnapshotNumber + 1;
        var now = DateTimeOffset.UtcNow;

        var installedApps = await dbContext.InstalledApps
            .AsNoTracking()
            .OrderBy(a => a.AppId)
            .ToListAsync(cancellationToken);

        var allMetadataItems = await dbContext.MetadataItems
            .AsNoTracking()
            .OrderBy(m => m.Kind)
            .ThenBy(m => m.ItemId)
            .ToListAsync(cancellationToken);

        var objects = allMetadataItems
            .Where(m => m.Kind == "object")
            .Select(m => JsonSerializer.Deserialize<ObjectDefinition>(m.DefinitionJson, JsonOptions)!)
            .Where(o => o is not null)
            .ToList();

        var enums = allMetadataItems
            .Where(m => m.Kind == "enum")
            .Select(m => JsonSerializer.Deserialize<EnumDefinition>(m.DefinitionJson, JsonOptions)!)
            .Where(e => e is not null)
            .ToList();

        var events = allMetadataItems
            .Where(m => m.Kind == "event")
            .Select(m => JsonSerializer.Deserialize<EventDefinition>(m.DefinitionJson, JsonOptions)!)
            .Where(e => e is not null)
            .ToList();

        var effectivePages = await GetEffectivePagesAsync(cancellationToken);
        var permissionSets = await GetPermissionSetsAsync(cancellationToken);

        var payload = new TenantMetadataSnapshotPayload
        {
            SnapshotNumber = nextNumber,
            CapturedAt = now,
            TriggerAction = triggerAction,
            TriggerAppId = triggerAppId,
            TriggerAppVersion = triggerAppVersion,
            InstalledApps = installedApps.Select(a => new InstalledAppSummary(
                a.AppId,
                a.Version,
                a.Name,
                a.Publisher,
                a.IsDeclarative,
                a.PackageSha256)).ToList(),
            Objects = objects,
            Enums = enums,
            Events = events.ToList(),
            Pages = effectivePages.ToList(),
            PermissionSets = permissionSets.ToList()
        };

        var snapshot = new MetadataSnapshot
        {
            Id = Guid.NewGuid(),
            SnapshotNumber = nextNumber,
            TriggerAction = triggerAction,
            TriggerAppId = triggerAppId,
            TriggerAppVersion = triggerAppVersion,
            SnapshotJson = JsonSerializer.Serialize(payload, JsonOptions)
        };

        dbContext.MetadataSnapshots.Add(snapshot);
        await dbContext.SaveChangesAsync(cancellationToken);
        return snapshot;
    }

    private static void PlaceExtendedFieldOnPage(PageDefinition page, PageFieldPlacement placement)
    {
        if (string.IsNullOrWhiteSpace(placement.Field))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(placement.After))
        {
            var afterShort = placement.After.Contains('.')
                ? placement.After[(placement.After.LastIndexOf('.') + 1)..]
                : placement.After;

            foreach (var group in page.Groups)
            {
                var idx = group.Fields.FindIndex(f =>
                    string.Equals(f, placement.After, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(f, afterShort, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                {
                    group.Fields.Insert(idx + 1, placement.Field);
                    return;
                }
            }
        }

        if (page.Groups.Count > 0)
        {
            page.Groups[0].Fields.Add(placement.Field);
        }
        else
        {
            page.Columns.Add(placement.Field);
        }
    }
}

/// <summary>What an install or upgrade did. <c>Outcome</c> is installed, upgraded or unchanged (the same package again).</summary>
public sealed record AppInstallResult(
    InstalledApp App,
    string Outcome,
    string? FromVersion,
    IReadOnlyList<SchemaChange> Changes,
    IReadOnlyList<MigrationRunInfo> Migrations,
    AppArchiveInfo? Archive);

public sealed record AppUninstallResult(MetadataSnapshot Snapshot, AppArchiveInfo? Archive);
