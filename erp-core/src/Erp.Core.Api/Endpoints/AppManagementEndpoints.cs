using System.Text.Json;
using Erp.Core.Domain.Apps;
using Erp.Core.Domain.Data;
using Erp.Core.Infrastructure.Apps;

namespace Erp.Core.Api.Endpoints;

public sealed record InstallPackageBase64Request(string PackageBase64, string? SigningKey = null);

public static class AppManagementEndpoints
{
    public static void MapAppManagementEndpoints(this IEndpointRouteBuilder v1)
    {
        v1.MapGet("/schemas/app-manifest.json", () =>
        {
            var schemaPath = ResolveManifestSchemaPath();
            if (schemaPath is not null && File.Exists(schemaPath))
            {
                var json = File.ReadAllText(schemaPath);
                return Results.Text(json, "application/schema+json");
            }

            return Results.NotFound(new { error = "Manifest JSON schema file not found." });
        }).AllowAnonymous();

        v1.MapGet("/apps", async (AppInstallationService appService, CancellationToken ct) =>
        {
            var apps = await appService.GetInstalledAppsAsync(ct);
            return Results.Ok(apps.Select(a => new
            {
                a.Id,
                a.AppId,
                a.Version,
                a.Name,
                a.Publisher,
                a.IsDeclarative,
                a.ContainerImage,
                a.PackageSha256,
                a.PackageStorageKey,
                a.CreatedAt,
                a.UpdatedAt
            }));
        });

        v1.MapPost("/apps/install", async (
            HttpRequest request,
            AppInstallationService appService,
            CancellationToken ct) =>
        {
            try
            {
                byte[] zipBytes;
                string? signingKey = request.Headers["X-Package-Signing-Key"].FirstOrDefault();

                if (request.ContentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true)
                {
                    var body = await request.ReadFromJsonAsync<InstallPackageBase64Request>(cancellationToken: ct);
                    if (body is null || string.IsNullOrWhiteSpace(body.PackageBase64))
                    {
                        return Results.BadRequest(new { error = "Request body must specify 'packageBase64'." });
                    }

                    zipBytes = Convert.FromBase64String(body.PackageBase64);
                    signingKey ??= body.SigningKey;
                }
                else
                {
                    using var ms = new MemoryStream();
                    await request.Body.CopyToAsync(ms, ct);
                    zipBytes = ms.ToArray();
                }

                return Results.Ok(ToResponse(await appService.InstallPackageAsync(zipBytes, signingKey, ct)));
            }
            catch (AppUpgradeException ex)
            {
                return Results.Conflict(new { error = ex.Message, errors = ex.Errors });
            }
            catch (AppMigrationException ex)
            {
                return Results.Conflict(new { error = ex.Message, migration = ex.Run });
            }
            catch (AppPackageSignatureException ex)
            {
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: StatusCodes.Status422UnprocessableEntity,
                    title: "Invalid App Package Signature");
            }
            catch (AppManifestValidationException ex)
            {
                return Results.BadRequest(new { error = ex.Message, errors = ex.Errors });
            }
            catch (MetadataValidationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (FormatException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).AddEndpointFilter(EventEndpoints.RequireTenantAdminAsync);

        v1.MapPost("/apps/install-builtin/{appId}", async (
            string appId,
            AppInstallationService appService,
            CancellationToken ct) =>
        {
            var appDir = ResolveBuiltInAppDirectory(appId);
            if (appDir is null)
            {
                return Results.NotFound(new { error = $"Built-in app package '{appId}' was not found." });
            }

            try
            {
                var zipBytes = AppPackageFormat.CreateSignedZipFromDirectory(appDir);
                return Results.Ok(ToResponse(await appService.InstallPackageAsync(zipBytes, null, ct)));
            }
            catch (AppUpgradeException ex)
            {
                return Results.Conflict(new { error = ex.Message, errors = ex.Errors });
            }
            catch (AppMigrationException ex)
            {
                return Results.Conflict(new { error = ex.Message, migration = ex.Run });
            }
            catch (AppManifestValidationException ex)
            {
                return Results.BadRequest(new { error = ex.Message, errors = ex.Errors });
            }
        }).AddEndpointFilter(EventEndpoints.RequireTenantAdminAsync);

        // Uninstalling archives the app's data for the grace period; purge=true deletes the tenant's data at once.
        v1.MapDelete("/apps/{appId}", async (
            string appId,
            bool? purge,
            AppInstallationService appService,
            CancellationToken ct) =>
        {
            try
            {
                var result = await appService.UninstallAppAsync(appId, purge ?? false, ct);
                return Results.Ok(new
                {
                    uninstalledAppId = appId,
                    snapshotNumber = result.Snapshot.SnapshotNumber,
                    triggerAction = result.Snapshot.TriggerAction,
                    archive = result.Archive
                });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (AppDependencyException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        }).AddEndpointFilter(EventEndpoints.RequireTenantAdminAsync);

        v1.MapGet("/apps/containers", (KubernetesAppOrchestrator orchestrator) =>
            Results.Ok(orchestrator.GetAllDeployments()));

        v1.MapGet("/metadata/pages", async (AppInstallationService appService, CancellationToken ct) =>
            Results.Ok(await appService.GetEffectivePagesAsync(ct)));

        v1.MapGet("/metadata/pages/{pageId}", async (string pageId, AppInstallationService appService, CancellationToken ct) =>
        {
            var pages = await appService.GetEffectivePagesAsync(ct);
            var page = pages.FirstOrDefault(p => string.Equals(p.Id, pageId, StringComparison.OrdinalIgnoreCase));
            return page is not null
                ? Results.Ok(page)
                : Results.NotFound(new { error = $"Page '{pageId}' was not found." });
        });

        v1.MapGet("/metadata/i18n/{locale}", async (string locale, AppInstallationService appService, CancellationToken ct) =>
            Results.Ok(await appService.GetTranslationsAsync(locale, ct)));

        v1.MapGet("/metadata/permissions", async (AppInstallationService appService, CancellationToken ct) =>
            Results.Ok(await appService.GetPermissionSetsAsync(ct)));

        v1.MapGet("/metadata/snapshots", async (AppInstallationService appService, CancellationToken ct) =>
        {
            var snapshots = await appService.GetSnapshotsAsync(ct);
            return Results.Ok(snapshots.Select(s => new
            {
                s.Id,
                s.SnapshotNumber,
                s.TriggerAction,
                s.TriggerAppId,
                s.TriggerAppVersion,
                s.CreatedAt,
                snapshot = JsonSerializer.Deserialize<JsonElement>(s.SnapshotJson)
            }));
        });

        // "What did the schema look like then": a snapshot by number, the one in force at a time, or the difference of two.
        v1.MapGet("/metadata/snapshots/{number:int}", async (int number, AppInstallationService appService, CancellationToken ct) =>
            await appService.GetSnapshotAsync(number, ct) is { } snapshot
                ? Results.Ok(ToSnapshotResponse(snapshot, objectId: null))
                : Results.NotFound(new { error = $"Snapshot {number} was not found." }));

        v1.MapGet("/metadata/snapshots/at", async (DateTimeOffset at, string? objectId, AppInstallationService appService, CancellationToken ct) =>
            await appService.GetSnapshotAtAsync(at, ct) is { } snapshot
                ? Results.Ok(ToSnapshotResponse(snapshot, objectId))
                : Results.NotFound(new { error = $"No snapshot existed at {at:O}." }));

        v1.MapGet("/metadata/snapshots/diff", async (int from, int to, AppInstallationService appService, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(new { from, to, changes = await appService.DiffSnapshotsAsync(from, to, ct) });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        // Data migrations run by upgrades: progress, checkpoint and errors per migration.
        v1.MapGet("/apps/migrations", async (string? appId, AppMigrationService migrations, CancellationToken ct) =>
            Results.Ok(await migrations.ListAsync(appId, ct)));

        v1.MapGet("/apps/archives", async (AppRetentionService retention, CancellationToken ct) =>
            Results.Ok(await retention.ListAsync(ct)));

        v1.MapPost("/apps/archives/{id:guid}/purge", async (Guid id, AppRetentionService retention, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await retention.PurgeAsync(id, ct));
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (RecordValidationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        }).AddEndpointFilter(EventEndpoints.RequireTenantAdminAsync);
    }

    private static object ToResponse(AppInstallResult result) => new
    {
        result.App.Id,
        result.App.AppId,
        result.App.Version,
        result.App.Name,
        result.App.Publisher,
        result.App.IsDeclarative,
        result.App.PackageSha256,
        result.App.PackageStorageKey,
        outcome = result.Outcome,
        fromVersion = result.FromVersion,
        changes = result.Changes,
        migrations = result.Migrations,
        archive = result.Archive
    };

    private static object ToSnapshotResponse(Erp.Core.Domain.Entities.MetadataSnapshot snapshot, string? objectId)
    {
        var json = JsonSerializer.Deserialize<JsonElement>(snapshot.SnapshotJson);
        object? content = json;
        if (!string.IsNullOrWhiteSpace(objectId) && json.TryGetProperty("objects", out var objects))
        {
            content = objects.EnumerateArray().FirstOrDefault(o =>
                o.TryGetProperty("id", out var id) && string.Equals(id.GetString(), objectId, StringComparison.OrdinalIgnoreCase));
        }

        return new
        {
            snapshot.SnapshotNumber,
            snapshot.TriggerAction,
            snapshot.TriggerAppId,
            snapshot.TriggerAppVersion,
            capturedAt = snapshot.CreatedAt,
            content
        };
    }

    private static string? ResolveManifestSchemaPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "erp-sdk", "schemas", "app-manifest.schema.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }

        return null;
    }

    private static string? ResolveBuiltInAppDirectory(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId)
            || appId.Contains("..", StringComparison.Ordinal)
            || appId.Contains('/')
            || appId.Contains('\\'))
        {
            return null;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidateApps = Path.Combine(dir.FullName, "erp-apps", appId);
            if (Directory.Exists(candidateApps))
            {
                return candidateApps;
            }

            var candidateSamples = Path.Combine(dir.FullName, "erp-samples", appId);
            if (Directory.Exists(candidateSamples))
            {
                return candidateSamples;
            }

            dir = dir.Parent;
        }

        return null;
    }
}

