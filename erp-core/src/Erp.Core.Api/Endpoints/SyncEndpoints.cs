using System.Text.Json;
using Erp.Core.Domain.Data;
using Erp.Core.Infrastructure.Sync;

namespace Erp.Core.Api.Endpoints;

public sealed record RegisterDeviceRequest(string Name);

public sealed record BlockDeviceRequest(string? Reason = null);

public sealed record SyncPullRequest(Guid DeviceId, string SetId, string? Cursor = null, string? Continuation = null, int? Limit = null);

public sealed record ReserveRequest(Guid DeviceId, string ObjectId, IReadOnlyList<Guid> RecordIds, int TtlMinutes = 60);

public sealed record ReleaseRequest(Guid DeviceId, IReadOnlyList<Guid>? RecordIds = null);

public sealed record PushCommandRequest(Guid Id, string Name, JsonElement Args, IReadOnlyList<CommandTargetInput>? Targets = null);

public sealed record SyncPushRequest(Guid DeviceId, IReadOnlyList<PushCommandRequest> Commands);

public sealed record ResolveCommandRequest(string Action);

/// <summary>
/// The API for offline clients: devices, sync sets (delta sync), reservations and the command queue (ADR-0014).
/// A client registers a device, downloads a set, reserves what it will work on, goes offline, and on reconnecting renews its
/// token and uploads the queued commands.
/// </summary>
public static class SyncEndpoints
{
    public static void MapSyncEndpoints(this IEndpointRouteBuilder v1)
    {
        var sync = v1.MapGroup("/sync");

        sync.MapGet("/sets", async (SyncService service, CancellationToken ct) =>
            Results.Ok(await service.ListSetsAsync(ct)));

        // ---------------------------------------------------------------- devices

        sync.MapPost("/devices", async (RegisterDeviceRequest request, SyncService service, CancellationToken ct) =>
            await ExecuteAsync(async () =>
            {
                var device = await service.RegisterDeviceAsync(request.Name, ct);
                return Results.Created($"/api/v1/sync/devices/{device.Id}", device);
            }));

        sync.MapGet("/devices", async (bool? all, SyncService service, Erp.Core.Infrastructure.Security.PermissionService permissions, CancellationToken ct) =>
            Results.Ok(await service.ListDevicesAsync((all ?? false) && permissions.IsAdmin, ct)));

        sync.MapPost("/devices/{id:guid}/token", async (Guid id, SyncService service, CancellationToken ct) =>
            await ExecuteAsync(async () => Results.Ok(await service.IssueTokenAsync(id, ct))));

        sync.MapPost("/devices/{id:guid}/block", async (Guid id, BlockDeviceRequest? request, SyncService service, CancellationToken ct) =>
            await ExecuteAsync(async () => Results.Ok(await service.SetBlockedAsync(id, true, request?.Reason, ct))))
            .AddEndpointFilter(EventEndpoints.RequireTenantAdminAsync);

        sync.MapPost("/devices/{id:guid}/unblock", async (Guid id, SyncService service, CancellationToken ct) =>
            await ExecuteAsync(async () => Results.Ok(await service.SetBlockedAsync(id, false, null, ct))))
            .AddEndpointFilter(EventEndpoints.RequireTenantAdminAsync);

        // ---------------------------------------------------------------- download

        sync.MapPost("/pull", async (SyncPullRequest request, SyncService service, CancellationToken ct) =>
            await ExecuteAsync(async () =>
                Results.Ok(await service.PullAsync(request.DeviceId, request.SetId, request.Cursor, request.Continuation, request.Limit, ct))));

        // ---------------------------------------------------------------- reservations

        sync.MapPost("/reservations", async (ReserveRequest request, SyncService service, CancellationToken ct) =>
            await ExecuteAsync(async () =>
            {
                var result = await service.ReserveAsync(request.DeviceId, request.ObjectId, request.RecordIds, request.TtlMinutes, ct);
                return result.Conflicts.Count > 0 && result.Reserved.Count == 0
                    ? Results.Json(result, statusCode: StatusCodes.Status409Conflict)
                    : Results.Ok(result);
            }));

        sync.MapGet("/reservations", async (Guid deviceId, SyncService service, CancellationToken ct) =>
            await ExecuteAsync(async () => Results.Ok(await service.ListReservationsAsync(deviceId, ct))));

        sync.MapPost("/reservations/release", async (ReleaseRequest request, SyncService service, CancellationToken ct) =>
            await ExecuteAsync(async () => Results.Ok(new { released = await service.ReleaseAsync(request.DeviceId, request.RecordIds, ct) })));

        // ---------------------------------------------------------------- upload

        sync.MapPost("/push", async (SyncPushRequest request, SyncService service, CancellationToken ct) =>
            await ExecuteAsync(async () =>
            {
                var commands = request.Commands
                    .Select(c => new CommandSubmission(c.Id, c.Name, c.Args, c.Targets))
                    .ToList();
                return Results.Ok(new { results = await service.PushAsync(request.DeviceId, commands, ct) });
            }));

        // The manual queue: commands whose records changed and whose app rule says a person decides.
        sync.MapGet("/commands", async (string? status, Guid? deviceId, int? top, SyncService service, CancellationToken ct) =>
            Results.Ok(await service.ListCommandsAsync(status, deviceId, top ?? 50, ct)))
            .AddEndpointFilter(EventEndpoints.RequireTenantAdminAsync);

        sync.MapPost("/commands/{id:guid}/resolve", async (Guid id, ResolveCommandRequest request, SyncService service, CancellationToken ct) =>
            await ExecuteAsync(async () => Results.Ok(await service.ResolveAsync(id, request.Action, ct))))
            .AddEndpointFilter(EventEndpoints.RequireTenantAdminAsync);
    }

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (SyncDeviceBlockedException ex)
        {
            return Results.Json(new { error = ex.Message, code = "device-blocked" }, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (Exception ex) when (ex is PermissionDeniedException or UnauthorizedAccessException)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or ObjectNotFoundException)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (RecordValidationException ex)
        {
            return Results.BadRequest(new { error = ex.Message, field = ex.FieldName });
        }
        catch (SyncCommandStateException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
    }
}
