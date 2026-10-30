using System.Data.Common;
using System.Text;
using Erp.Core.Domain.Tenancy;
using Erp.Core.Infrastructure.Apps;
using Erp.Core.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NpgsqlTypes;

namespace Erp.Core.Infrastructure.Events;

/// <summary>
/// Delivers outbox events to app subscribers (gRPC) and webhooks (HTTP). Each delivery is claimed across
/// tenants through <c>core.claim_due_deliveries</c> and then processed under the tenant's own RLS context.
/// Failures never affect the transaction that published the event (architecture rule 7).
/// </summary>
public sealed class EventDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    EventDeliveryWakeSignal wakeSignal,
    EventBusOptions options,
    ILogger<EventDeliveryWorker> logger) : BackgroundService
{
    public const string WebhookHttpClientName = "erp-webhooks";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastPurge = DateTimeOffset.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = 0;
            try
            {
                processed = await RunOnceAsync(stoppingToken);

                if (DateTimeOffset.UtcNow - lastPurge >= options.PurgeInterval)
                {
                    await PurgeExpiredEventsAsync(stoppingToken);
                    lastPurge = DateTimeOffset.UtcNow;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Event delivery worker iteration failed.");
            }

            if (processed == 0)
            {
                try
                {
                    await wakeSignal.WaitAsync(options.PollInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>Claims and processes one batch of due deliveries. Returns the number of deliveries attempted.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var claimed = await ClaimAsync(cancellationToken);
        foreach (var (deliveryId, tenantId) in claimed)
        {
            try
            {
                await DeliverAsync(deliveryId, tenantId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The claim lease expires and the delivery is retried.
                logger.LogError(ex, "Delivery {DeliveryId} for tenant {TenantId} failed unexpectedly.", deliveryId, tenantId);
            }
        }

        return claimed.Count;
    }

    private async Task<List<(Guid DeliveryId, Guid TenantId)>> ClaimAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CoreDbContext>();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();

        // No tenant context: the SECURITY DEFINER function only returns identifiers.
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        var result = new List<(Guid, Guid)>();
        await using var cmd = TenantDb.Create(
            connection,
            null,
            "SELECT delivery_id, tenant_id FROM core.claim_due_deliveries(@limit, @lease);");
        TenantDb.Add(cmd, "limit", NpgsqlDbType.Integer, options.BatchSize);
        TenantDb.Add(cmd, "lease", NpgsqlDbType.Interval, options.ClaimLease);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add((reader.GetGuid(0), reader.GetGuid(1)));
        }

        return result;
    }

    private async Task DeliverAsync(Guid deliveryId, Guid tenantId, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        var dbContext = scope.ServiceProvider.GetRequiredService<CoreDbContext>();

        tenantContext.Set(tenantId);
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);

        var delivery = await LoadAsync(connection, deliveryId, cancellationToken);
        if (delivery is null || delivery.Status != "pending")
        {
            return;
        }

        // Act as the user that triggered the event so the short-lived call token carries the right identity.
        tenantContext.Set(tenantId, delivery.UserId, delivery.CompanyId);
        await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);

        try
        {
            if (delivery.SubscriberType == "app")
            {
                var separator = delivery.SubscriberKey.IndexOf('|', StringComparison.Ordinal);
                var appId = separator < 0 ? delivery.SubscriberKey : delivery.SubscriberKey[..separator];
                var handler = separator < 0 ? string.Empty : delivery.SubscriberKey[(separator + 1)..];

                var appHost = scope.ServiceProvider.GetRequiredService<AppHostService>();
                await appHost.DeliverEventToHandlerAsync(
                    appId,
                    handler,
                    delivery.EventId.ToString("D"),
                    delivery.EventName,
                    delivery.PayloadJson,
                    delivery.CompanyId,
                    delivery.CorrelationId,
                    cancellationToken);
            }
            else
            {
                await DeliverWebhookAsync(scope.ServiceProvider, connection, delivery, cancellationToken);
            }

            await MarkDeliveredAsync(connection, deliveryId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await MarkFailedAsync(connection, delivery, ex.Message, cancellationToken);
        }
    }

    private async Task DeliverWebhookAsync(
        IServiceProvider services,
        DbConnection connection,
        DeliveryRow delivery,
        CancellationToken cancellationToken)
    {
        string? url;
        await using (var cmd = TenantDb.Create(
            connection, null, "SELECT url FROM core.webhook_subscription WHERE id = @id AND is_active;"))
        {
            TenantDb.Add(cmd, "id", NpgsqlDbType.Uuid, Guid.Parse(delivery.SubscriberKey));
            url = await cmd.ExecuteScalarAsync(cancellationToken) as string;
        }

        if (url is null)
        {
            throw new InvalidOperationException("Webhook subscription no longer exists or is inactive.");
        }

        var secret = await EventLogService.GetOrCreateWebhookSecretAsync(
            connection, services.GetRequiredService<ITenantContext>(), cancellationToken);

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var body = delivery.EnvelopeJson;
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add(WebhookSigner.TimestampHeader, timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add(WebhookSigner.SignatureHeader, WebhookSigner.Sign(secret, timestamp, body));
        request.Headers.Add(WebhookSigner.EventIdHeader, delivery.EventId.ToString("D"));
        request.Headers.Add(WebhookSigner.EventNameHeader, delivery.EventName);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(options.WebhookTimeout);

        var client = services.GetRequiredService<IHttpClientFactory>().CreateClient(WebhookHttpClientName);
        using var response = await client.SendAsync(request, timeoutCts.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Webhook endpoint answered {(int)response.StatusCode}.");
        }
    }

    private static async Task<DeliveryRow?> LoadAsync(DbConnection connection, Guid deliveryId, CancellationToken cancellationToken)
    {
        await using var cmd = TenantDb.Create(
            connection,
            null,
            """
            SELECT d.status, d.attempts, d.subscriber_type, d.subscriber_key, e.id, e.event_name, e.payload::text,
                   e.correlation_id, e.company_id, e.created_by, e.tenant_id, e.occurred_at, e.aggregate_id
              FROM core.event_delivery d
              JOIN core.event_log e ON e.id = d.event_id
             WHERE d.id = @id;
            """);
        TenantDb.Add(cmd, "id", NpgsqlDbType.Uuid, deliveryId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var eventName = reader.GetString(5);
        var payloadJson = reader.GetString(6);
        var tenantId = reader.GetGuid(10);
        var occurredAt = reader.GetFieldValue<DateTimeOffset>(11);
        var eventId = reader.GetGuid(4);
        var companyId = reader.IsDBNull(8) ? (Guid?)null : reader.GetGuid(8);
        var correlationId = reader.GetString(7);

        return new DeliveryRow(
            deliveryId,
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetString(2),
            reader.GetString(3),
            eventId,
            eventName,
            payloadJson,
            correlationId,
            companyId,
            reader.GetGuid(9),
            EventLogService.BuildEnvelopeJson(
                eventId, eventName, tenantId, companyId, reader.GetGuid(9), correlationId, occurredAt, reader.GetString(12), payloadJson));
    }

    private async Task MarkDeliveredAsync(DbConnection connection, Guid deliveryId, CancellationToken cancellationToken)
    {
        await using var cmd = TenantDb.Create(
            connection,
            null,
            """
            UPDATE core.event_delivery
               SET status = 'delivered', attempts = attempts + 1, delivered_at = now(), last_error = NULL,
                   updated_at = now(), row_version = row_version + 1
             WHERE id = @id;
            """);
        TenantDb.Add(cmd, "id", NpgsqlDbType.Uuid, deliveryId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task MarkFailedAsync(
        DbConnection connection,
        DeliveryRow delivery,
        string error,
        CancellationToken cancellationToken)
    {
        var failedAttempts = delivery.Attempts + 1;
        var dead = failedAttempts >= options.MaxAttempts;
        logger.LogWarning(
            "Delivery {DeliveryId} of {Event} to {Subscriber} failed (attempt {Attempt}{Dead}): {Error}",
            delivery.Id, delivery.EventName, delivery.SubscriberKey, failedAttempts, dead ? ", dead-lettered" : string.Empty, error);

        await using var cmd = TenantDb.Create(
            connection,
            null,
            """
            UPDATE core.event_delivery
               SET status = @status, attempts = @attempts, next_attempt_at = @next, last_error = @error,
                   updated_at = now(), row_version = row_version + 1
             WHERE id = @id;
            """);
        TenantDb.Add(cmd, "id", NpgsqlDbType.Uuid, delivery.Id);
        TenantDb.Add(cmd, "status", NpgsqlDbType.Varchar, dead ? "dead" : "pending");
        TenantDb.Add(cmd, "attempts", NpgsqlDbType.Integer, failedAttempts);
        TenantDb.Add(cmd, "next", NpgsqlDbType.TimestampTz, DateTimeOffset.UtcNow + options.BackoffFor(failedAttempts));
        TenantDb.Add(cmd, "error", NpgsqlDbType.Text, error.Length > 2000 ? error[..2000] : error);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task PurgeExpiredEventsAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CoreDbContext>();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);

        await using var cmd = TenantDb.Create(connection, null, "SELECT core.purge_expired_events(@retention);");
        TenantDb.Add(cmd, "retention", NpgsqlDbType.Interval, options.Retention);
        var purged = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken));
        if (purged > 0)
        {
            logger.LogInformation("Purged {Count} events older than {Retention}.", purged, options.Retention);
        }
    }

    private sealed record DeliveryRow(
        Guid Id,
        string Status,
        int Attempts,
        string SubscriberType,
        string SubscriberKey,
        Guid EventId,
        string EventName,
        string PayloadJson,
        string CorrelationId,
        Guid? CompanyId,
        Guid UserId,
        string EnvelopeJson);
}
