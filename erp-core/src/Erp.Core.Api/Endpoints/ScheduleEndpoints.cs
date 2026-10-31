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
    }
}
