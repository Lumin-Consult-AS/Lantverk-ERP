using Erp.Core.Domain.Data;
using Erp.Core.Domain.Tenancy;
using Erp.Core.Infrastructure.Events;

namespace Erp.Core.Api.Endpoints;

public sealed record CreateWebhookRequest(string Url, string? EventPattern = null);

public sealed record ReplayEventRequest(string? App = null, Guid? Webhook = null);

/// <summary>Event log, dead-letter queue, replay and webhook administration. Restricted to <c>tenant.admin</c>.</summary>
public static class EventEndpoints
{
    private const string AdminRole = "tenant.admin";

    public static void MapEventEndpoints(this IEndpointRouteBuilder v1)
    {
        var events = v1.MapGroup("/events").AddEndpointFilter(RequireTenantAdminAsync);

        events.MapGet("/", async (
            EventLogService service,
            string? name,
            int? top,
            CancellationToken ct) => Results.Ok(await service.ListEventsAsync(name, top ?? 50, ct)));

        events.MapGet("/dead-letters", async (EventLogService service, CancellationToken ct) =>
            Results.Ok(await service.ListDeadLettersAsync(ct)));

        events.MapGet("/{id:guid}", async (Guid id, EventLogService service, CancellationToken ct) =>
            await ExecuteAsync(async () =>
            {
                var (entry, deliveries) = await service.GetEventAsync(id, ct);
                return Results.Ok(new { entry.Id, entry.EventName, entry.AggregateId, entry.CompanyId, entry.UserId,
                    entry.CorrelationId, entry.OccurredAt, entry.Payload, deliveries });
            }));

        events.MapPost("/{id:guid}/replay", async (
            Guid id,
            ReplayEventRequest request,
            EventLogService service,
            CancellationToken ct) =>
            await ExecuteAsync(async () =>
                Results.Accepted(value: new { queued = await service.ReplayAsync(id, request.App, request.Webhook, ct) })));

        var webhooks = v1.MapGroup("/webhooks").AddEndpointFilter(RequireTenantAdminAsync);

        webhooks.MapGet("/", async (EventLogService service, CancellationToken ct) =>
            Results.Ok(await service.ListWebhooksAsync(ct)));

        webhooks.MapPost("/", async (CreateWebhookRequest request, EventLogService service, CancellationToken ct) =>
            await ExecuteAsync(async () =>
            {
                var created = await service.CreateWebhookAsync(request.Url, request.EventPattern, ct);
                return Results.Created($"/api/v1/webhooks/{created.Id}", created);
            }));

        webhooks.MapDelete("/{id:guid}", async (Guid id, EventLogService service, CancellationToken ct) =>
            await service.DeleteWebhookAsync(id, ct) ? Results.NoContent() : Results.NotFound());

        // Receivers verify "X-Erp-Signature" with this secret (see WebhookSigner).
        webhooks.MapGet("/secret", async (EventLogService service, CancellationToken ct) =>
            Results.Ok(new { secret = await service.GetWebhookSecretAsync(ct) }));
    }

    internal static async ValueTask<object?> RequireTenantAdminAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var tenant = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
        return tenant.Roles.Contains(AdminRole, StringComparer.OrdinalIgnoreCase)
            ? await next(context)
            : Results.Json(new { error = $"Role '{AdminRole}' is required." }, statusCode: StatusCodes.Status403Forbidden);
    }

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (RecordValidationException ex)
        {
            return Results.BadRequest(new { error = ex.Message, field = ex.FieldName });
        }
    }
}
