using System.Data.Common;
using System.Text.Json;
using Erp.Core.Domain.Apps;
using Erp.Core.Domain.Tenancy;
using Erp.Core.Infrastructure.Apps;
using Erp.Core.Infrastructure.Persistence;
using NpgsqlTypes;

namespace Erp.Core.Infrastructure.Events;

/// <summary>
/// Writes an event to the transactional outbox (<c>core.event_log</c> plus one <c>core.event_delivery</c>
/// row per subscriber) using the caller's connection and transaction, so the event exists if and only if
/// the triggering transaction commits. Delivery itself is done by <see cref="EventDeliveryWorker"/>.
/// </summary>
public sealed class EventPublisher(
    CoreDbContext dbContext,
    ITenantContext tenantContext,
    EventDeliveryWakeSignal wakeSignal)
{
    private bool _hasUnnotifiedEvents;

    public async Task<Guid> PublishAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string eventName,
        string aggregateId,
        object payload,
        Guid? companyId,
        string? correlationId,
        CancellationToken cancellationToken,
        Guid? eventId = null)
    {
        var tenantId = tenantContext.TenantId
            ?? throw new InvalidOperationException("Cannot publish an event without an active tenant context.");
        var userId = tenantContext.UserId ?? tenantId;
        var id = eventId ?? Guid.NewGuid();
        var payloadJson = payload as string ?? JsonSerializer.Serialize(payload, AppHostService.JsonOptions);
        var effectiveCompanyId = companyId ?? tenantContext.CompanyId;
        var effectiveCorrelationId = string.IsNullOrWhiteSpace(correlationId) ? Guid.NewGuid().ToString("D") : correlationId;

        await using (var cmd = TenantDb.Create(
            connection,
            transaction,
            """
            INSERT INTO core.event_log
                (id, tenant_id, company_id, created_by, updated_by, event_name, aggregate_id, correlation_id, occurred_at, payload)
            VALUES
                (@id, @tenantId, @companyId, @userId, @userId, @eventName, @aggregateId, @correlationId, now(), @payload::jsonb);
            """))
        {
            TenantDb.Add(cmd, "id", NpgsqlDbType.Uuid, id);
            TenantDb.Add(cmd, "tenantId", NpgsqlDbType.Uuid, tenantId);
            TenantDb.Add(cmd, "companyId", NpgsqlDbType.Uuid, effectiveCompanyId);
            TenantDb.Add(cmd, "userId", NpgsqlDbType.Uuid, userId);
            TenantDb.Add(cmd, "eventName", NpgsqlDbType.Varchar, eventName);
            TenantDb.Add(cmd, "aggregateId", NpgsqlDbType.Varchar, aggregateId);
            TenantDb.Add(cmd, "correlationId", NpgsqlDbType.Varchar, effectiveCorrelationId);
            TenantDb.Add(cmd, "payload", NpgsqlDbType.Text, payloadJson);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        var subscribers = new HashSet<(string Type, string Key)>();
        foreach (var (appId, handler) in await ResolveAppSubscribersAsync(connection, transaction, eventName, cancellationToken))
        {
            subscribers.Add(("app", $"{appId}|{handler}"));
        }

        foreach (var webhookId in await ResolveWebhookSubscribersAsync(connection, transaction, eventName, cancellationToken))
        {
            subscribers.Add(("webhook", webhookId.ToString("D")));
        }

        foreach (var (type, key) in subscribers)
        {
            await InsertDeliveryAsync(
                connection, transaction, tenantId, userId, effectiveCompanyId, id, aggregateId, type, key, isReplay: false, cancellationToken);
        }

        _hasUnnotifiedEvents = true;
        return id;
    }

    /// <summary>Publishes in a short transaction of its own (used for events published by apps over gRPC).</summary>
    public async Task<Guid> PublishInOwnTransactionAsync(
        string eventName,
        string aggregateId,
        object payload,
        Guid? companyId,
        string? correlationId,
        CancellationToken cancellationToken,
        Guid? eventId = null)
    {
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        var id = await PublishAsync(
            connection, tx, eventName, aggregateId, payload, companyId, correlationId, cancellationToken, eventId);
        await tx.CommitAsync(cancellationToken);
        NotifyCommitted();
        return id;
    }

    /// <summary>Call after the publishing transaction has committed to wake the delivery worker.</summary>
    public void NotifyCommitted()
    {
        if (_hasUnnotifiedEvents)
        {
            _hasUnnotifiedEvents = false;
            wakeSignal.Notify();
        }
    }

    internal static async Task InsertDeliveryAsync(
        DbConnection connection,
        DbTransaction? transaction,
        Guid tenantId,
        Guid userId,
        Guid? companyId,
        Guid eventId,
        string aggregateId,
        string subscriberType,
        string subscriberKey,
        bool isReplay,
        CancellationToken cancellationToken)
    {
        await using var cmd = TenantDb.Create(
            connection,
            transaction,
            """
            INSERT INTO core.event_delivery
                (id, tenant_id, company_id, created_by, updated_by, event_id, subscriber_type, subscriber_key, aggregate_id, is_replay)
            VALUES
                (@id, @tenantId, @companyId, @userId, @userId, @eventId, @type, @key, @aggregateId, @isReplay);
            """);
        TenantDb.Add(cmd, "id", NpgsqlDbType.Uuid, Guid.NewGuid());
        TenantDb.Add(cmd, "tenantId", NpgsqlDbType.Uuid, tenantId);
        TenantDb.Add(cmd, "companyId", NpgsqlDbType.Uuid, companyId);
        TenantDb.Add(cmd, "userId", NpgsqlDbType.Uuid, userId);
        TenantDb.Add(cmd, "eventId", NpgsqlDbType.Uuid, eventId);
        TenantDb.Add(cmd, "type", NpgsqlDbType.Varchar, subscriberType);
        TenantDb.Add(cmd, "key", NpgsqlDbType.Varchar, subscriberKey);
        TenantDb.Add(cmd, "aggregateId", NpgsqlDbType.Varchar, aggregateId);
        TenantDb.Add(cmd, "isReplay", NpgsqlDbType.Boolean, isReplay);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<List<(string AppId, string Handler)>> ResolveAppSubscribersAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string eventName,
        CancellationToken cancellationToken)
    {
        var result = new List<(string, string)>();
        await using var cmd = TenantDb.Create(
            connection,
            transaction,
            "SELECT app_id, manifest_json::text FROM core.installed_app WHERE NOT is_declarative ORDER BY created_at;");
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var appId = reader.GetString(0);
            var manifest = JsonSerializer.Deserialize<AppManifest>(reader.GetString(1), AppHostService.JsonOptions);
            foreach (var sub in manifest?.SubscribedEvents ?? [])
            {
                if (AppHostService.MatchesEventSpec(sub, eventName))
                {
                    result.Add((appId, sub.Handler ?? string.Empty));
                }
            }
        }

        return result;
    }

    private static async Task<List<Guid>> ResolveWebhookSubscribersAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string eventName,
        CancellationToken cancellationToken)
    {
        var result = new List<Guid>();
        await using var cmd = TenantDb.Create(
            connection,
            transaction,
            "SELECT id, event_pattern FROM core.webhook_subscription WHERE is_active;");
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (EventPattern.Matches(reader.GetString(1), eventName))
            {
                result.Add(reader.GetGuid(0));
            }
        }

        return result;
    }
}

/// <summary>Webhook event filters: <c>*</c>, an exact name, or a prefix ending in <c>*</c> (e.g. <c>core.party.*</c>).</summary>
public static class EventPattern
{
    public static bool Matches(string pattern, string eventName)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern == "*")
        {
            return true;
        }

        return pattern.EndsWith('*')
            ? eventName.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(pattern, eventName, StringComparison.OrdinalIgnoreCase);
    }
}
