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

                var installed = await appService.InstallPackageAsync(zipBytes, signingKey, ct);
                return Results.Ok(new
                {
                    installed.Id,
                    installed.AppId,
                    installed.Version,
                    installed.Name,
                    installed.Publisher,
                    installed.IsDeclarative,
                    installed.PackageSha256,
                    installed.PackageStorageKey
                });
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
        });

        v1.MapDelete("/apps/{appId}", async (
            string appId,
            AppInstallationService appService,
            CancellationToken ct) =>
        {
            try
            {
                var snapshot = await appService.UninstallAppAsync(appId, ct);
                return Results.Ok(new
                {
                    uninstalledAppId = appId,
                    snapshotNumber = snapshot.SnapshotNumber,
                    triggerAction = snapshot.TriggerAction
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
        });

        v1.MapGet("/metadata/pages", async (AppInstallationService appService, CancellationToken ct) =>
            Results.Ok(await appService.GetEffectivePagesAsync(ct)));

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
}

