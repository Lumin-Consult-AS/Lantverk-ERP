using Erp.Core.Infrastructure.Scheduling;

namespace Erp.Core.Api.Endpoints;

/// <summary>What the scheduled jobs of the installed apps have done, and a way for an administrator to run one now.</summary>
public static class ScheduleEndpoints
{
    public static void MapScheduleEndpoints(this IEndpointRouteBuilder v1)
    {
        var schedules = v1.MapGroup("/schedules").AddEndpointFilter(EventEndpoints.RequireTenantAdminAsync);

        schedules.MapGet("/", async (ScheduleService service, CancellationToken ct) =>
        {
            await service.SyncAsync(DateTimeOffset.UtcNow, ct);
            return Results.Ok(await service.ListAsync(ct));
        });

        schedules.MapPost("/{appId}/{scheduleId}/run", async (string appId, string scheduleId, ScheduleService service, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await service.RunNowAsync(appId, scheduleId, DateTimeOffset.UtcNow, ct));
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        schedules.MapPost("/{appId}/{scheduleId}/start", (string appId, string scheduleId, ScheduleService service, CancellationToken ct) =>
            SetEnabledAsync(service, appId, scheduleId, true, ct));

        schedules.MapPost("/{appId}/{scheduleId}/stop", (string appId, string scheduleId, ScheduleService service, CancellationToken ct) =>
            SetEnabledAsync(service, appId, scheduleId, false, ct));

        schedules.MapGet("/{appId}/{scheduleId}/runs", async (string appId, string scheduleId, ScheduleService service, CancellationToken ct, int limit = 50) =>
            Results.Ok(await service.ListRunsAsync(appId, scheduleId, limit, ct)));
    }

    private static async Task<IResult> SetEnabledAsync(ScheduleService service, string appId, string scheduleId, bool enabled, CancellationToken ct)
    {
        try
        {
            await service.SetEnabledAsync(appId, scheduleId, enabled, DateTimeOffset.UtcNow, ct);
            return Results.NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
    }
}
