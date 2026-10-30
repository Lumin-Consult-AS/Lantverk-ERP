using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using Erp.Core.Domain.Data;
using Erp.Core.Domain.Tenancy;
using Erp.Core.Infrastructure.Persistence;
using NpgsqlTypes;

namespace Erp.Core.Infrastructure.Events;

public sealed record EventLogEntry(
    Guid Id,
    string EventName,
    string AggregateId,
    Guid? CompanyId,
    Guid UserId,
    string CorrelationId,
    DateTimeOffset OccurredAt,
    JsonNode? Payload);

public sealed record EventDeliveryEntry(
    Guid Id,
    Guid EventId,
    string SubscriberType,
    string SubscriberKey,
    string Status,
    int Attempts,
    DateTimeOffset NextAttemptAt,
    string? LastError,
    DateTimeOffset? DeliveredAt,
    bool IsReplay);

public sealed record WebhookSubscriptionEntry(Guid Id, string Url, string EventPattern, bool IsActive, DateTimeOffset CreatedAt);

/// <summary>Reads the tenant's event log and dead-letter queue, replays events, and manages webhook subscriptions.</summary>
public sealed class EventLogService(
    CoreDbContext dbContext,
    ITenantContext tenantContext,
    EventDeliveryWakeSignal wakeSignal)
{
    private const int MaxPageSize = 200;

    public async Task<IReadOnlyList<EventLogEntry>> ListEventsAsync(
        string? eventName = null,
        int top = 50,
        CancellationToken cancellationToken = default)
    {
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        await using var cmd = TenantDb.Create(
            connection,
            null,
            """
            SELECT id, event_name, aggregate_id, company_id, created_by, correlation_id, occurred_at, payload::text
              FROM core.event_log
             WHERE (@name::text IS NULL OR event_name = @name)
             ORDER BY occurred_at DESC, id
             LIMIT @top;
            """);
        TenantDb.Add(cmd, "name", NpgsqlDbType.Text, string.IsNullOrWhiteSpace(eventName) ? null : eventName);
        TenantDb.Add(cmd, "top", NpgsqlDbType.Integer, Math.Clamp(top, 1, MaxPageSize));

        var result = new List<EventLogEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadEvent(reader));
        }

        return result;
    }

    public async Task<(EventLogEntry Event, IReadOnlyList<EventDeliveryEntry> Deliveries)> GetEventAsync(
        Guid eventId,
        CancellationToken cancellationToken = default)
    {
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);

        EventLogEntry? entry;
        await using (var cmd = TenantDb.Create(
            connection,
            null,
            """
            SELECT id, event_name, aggregate_id, company_id, created_by, correlation_id, occurred_at, payload::text
              FROM core.event_log WHERE id = @id;
            """))
        {
            TenantDb.Add(cmd, "id", NpgsqlDbType.Uuid, eventId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            entry = await reader.ReadAsync(cancellationToken) ? ReadEvent(reader) : null;
        }

        if (entry is null)
        {
            throw new KeyNotFoundException($"Event '{eventId}' was not found.");
        }

        return (entry, await ListDeliveriesAsync(connection, eventId, null, cancellationToken));
    }

    /// <summary>Lists deliveries that exhausted their retries (the dead-letter queue).</summary>
    public async Task<IReadOnlyList<EventDeliveryEntry>> ListDeadLettersAsync(CancellationToken cancellationToken = default)
    {
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        return await ListDeliveriesAsync(connection, null, "dead", cancellationToken);
    }

    /// <summary>
    /// Plays an event back to one subscriber: an app (<c>appId</c>, optionally all its handlers) or a webhook.
    /// Handlers must be idempotent; the SDK drops events it has already processed.
    /// </summary>
    public async Task<int> ReplayAsync(
        Guid eventId,
        string? appId,
        Guid? webhookId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(appId) == (webhookId is null))
        {
            throw new RecordValidationException("Specify exactly one of 'app' or 'webhook' as replay target.");
        }

        var tenantId = tenantContext.TenantId
            ?? throw new InvalidOperationException("Cannot replay events without an active tenant context.");
        var userId = tenantContext.UserId ?? tenantId;

        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        string eventName;
        string aggregateId;
        Guid? companyId;
        await using (var cmd = TenantDb.Create(
            connection, tx, "SELECT event_name, aggregate_id, company_id FROM core.event_log WHERE id = @id;"))
        {
            TenantDb.Add(cmd, "id", NpgsqlDbType.Uuid, eventId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new KeyNotFoundException($"Event '{eventId}' was not found.");
            }

            eventName = reader.GetString(0);
            aggregateId = reader.GetString(1);
            companyId = reader.IsDBNull(2) ? null : reader.GetGuid(2);
        }

        var subscriberKeys = new List<(string Type, string Key)>();
        if (webhookId is not null)
        {
            await using var cmd = TenantDb.Create(
                connection, tx, "SELECT count(*) FROM core.webhook_subscription WHERE id = @id;");
            TenantDb.Add(cmd, "id", NpgsqlDbType.Uuid, webhookId.Value);
            if (Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken)) == 0)
            {
                throw new KeyNotFoundException($"Webhook '{webhookId}' was not found.");
            }

            subscriberKeys.Add(("webhook", webhookId.Value.ToString("D")));
        }
        else
        {
            var handlers = await ResolveAppHandlersAsync(connection, tx, appId!, eventName, cancellationToken);
            if (handlers.Count == 0)
            {
                throw new RecordValidationException(
                    $"App '{appId}' is not installed or does not subscribe to '{eventName}'.");
            }

            subscriberKeys.AddRange(handlers.Select(h => ("app", $"{appId}|{h}")));
        }

        foreach (var (type, key) in subscriberKeys)
        {
            await EventPublisher.InsertDeliveryAsync(
                connection, tx, tenantId, userId, companyId, eventId, aggregateId, type, key, isReplay: true, cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        wakeSignal.Notify();
        return subscriberKeys.Count;
    }

    public async Task<IReadOnlyList<WebhookSubscriptionEntry>> ListWebhooksAsync(CancellationToken cancellationToken = default)
    {
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        await using var cmd = TenantDb.Create(
            connection,
            null,
            "SELECT id, url, event_pattern, is_active, created_at FROM core.webhook_subscription ORDER BY created_at;");
        var result = new List<WebhookSubscriptionEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new WebhookSubscriptionEntry(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.GetFieldValue<DateTimeOffset>(4)));
        }

        return result;
    }

    public async Task<WebhookSubscriptionEntry> CreateWebhookAsync(
        string url,
        string? eventPattern,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new RecordValidationException("Webhook 'url' must be an absolute http(s) URL.", "url");
        }

        var tenantId = tenantContext.TenantId
            ?? throw new InvalidOperationException("Cannot create a webhook without an active tenant context.");
        var userId = tenantContext.UserId ?? tenantId;
        var pattern = string.IsNullOrWhiteSpace(eventPattern) ? "*" : eventPattern.Trim();

        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        var id = Guid.NewGuid();
        await using var cmd = TenantDb.Create(
            connection,
            null,
            """
            INSERT INTO core.webhook_subscription (id, tenant_id, created_by, updated_by, url, event_pattern)
            VALUES (@id, @tenantId, @userId, @userId, @url, @pattern)
            RETURNING created_at;
            """);
        TenantDb.Add(cmd, "id", NpgsqlDbType.Uuid, id);
        TenantDb.Add(cmd, "tenantId", NpgsqlDbType.Uuid, tenantId);
        TenantDb.Add(cmd, "userId", NpgsqlDbType.Uuid, userId);
        TenantDb.Add(cmd, "url", NpgsqlDbType.Varchar, uri.ToString());
        TenantDb.Add(cmd, "pattern", NpgsqlDbType.Varchar, pattern);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var createdAt = reader.GetFieldValue<DateTimeOffset>(0);

        return new WebhookSubscriptionEntry(id, uri.ToString(), pattern, true, createdAt);
    }

    public async Task<bool> DeleteWebhookAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        // Pending deliveries to a removed webhook would only retry until they are dead-lettered.
        await using (var pending = TenantDb.Create(
            connection,
            tx,
            "DELETE FROM core.event_delivery WHERE subscriber_type = 'webhook' AND subscriber_key = @key AND status = 'pending';"))
        {
            TenantDb.Add(pending, "key", NpgsqlDbType.Varchar, id.ToString("D"));
            await pending.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var cmd = TenantDb.Create(connection, tx, "DELETE FROM core.webhook_subscription WHERE id = @id;");
        TenantDb.Add(cmd, "id", NpgsqlDbType.Uuid, id);
        var deleted = await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
        await tx.CommitAsync(cancellationToken);
        return deleted;
    }

    /// <summary>Returns the tenant's webhook signing secret, creating it on first use.</summary>
    public async Task<string> GetWebhookSecretAsync(CancellationToken cancellationToken = default)
    {
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        return await GetOrCreateWebhookSecretAsync(connection, tenantContext, cancellationToken);
    }

    internal static async Task<string> GetOrCreateWebhookSecretAsync(
        DbConnection connection,
        ITenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        var tenantId = tenantContext.TenantId
            ?? throw new InvalidOperationException("Cannot read the webhook secret without an active tenant context.");
        var userId = tenantContext.UserId ?? tenantId;

        await using (var insert = TenantDb.Create(
            connection,
            null,
            """
            INSERT INTO core.tenant_secret (id, tenant_id, created_by, updated_by, name, secret)
            VALUES (@id, @tenantId, @userId, @userId, 'webhook-signing', @secret)
            ON CONFLICT (tenant_id, name) DO NOTHING;
            """))
        {
            TenantDb.Add(insert, "id", NpgsqlDbType.Uuid, Guid.NewGuid());
            TenantDb.Add(insert, "tenantId", NpgsqlDbType.Uuid, tenantId);
            TenantDb.Add(insert, "userId", NpgsqlDbType.Uuid, userId);
            TenantDb.Add(insert, "secret", NpgsqlDbType.Text, WebhookSigner.GenerateSecret());
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var select = TenantDb.Create(
            connection, null, "SELECT secret FROM core.tenant_secret WHERE name = 'webhook-signing';");
        return (string)(await select.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>The JSON body POSTed to webhooks: event metadata plus the publisher's payload.</summary>
    internal static string BuildEnvelopeJson(
        Guid eventId,
        string eventName,
        Guid tenantId,
        Guid? companyId,
        Guid userId,
        string correlationId,
        DateTimeOffset occurredAt,
        string aggregateId,
        string payloadJson)
    {
        var envelope = new JsonObject
        {
            ["id"] = eventId.ToString("D"),
            ["event"] = eventName,
            ["tenantId"] = tenantId.ToString("D"),
            ["companyId"] = companyId?.ToString("D"),
            ["userId"] = userId.ToString("D"),
            ["correlationId"] = correlationId,
            ["occurredAt"] = occurredAt,
            ["aggregateId"] = aggregateId,
            ["payload"] = JsonNode.Parse(payloadJson)
        };
        return envelope.ToJsonString();
    }

    private static async Task<List<string>> ResolveAppHandlersAsync(
        DbConnection connection,
        DbTransaction tx,
        string appId,
        string eventName,
        CancellationToken cancellationToken)
    {
        string? manifestJson;
        await using (var cmd = TenantDb.Create(
            connection, tx, "SELECT manifest_json::text FROM core.installed_app WHERE app_id = @appId AND NOT is_declarative;"))
        {
            TenantDb.Add(cmd, "appId", NpgsqlDbType.Varchar, appId);
            manifestJson = await cmd.ExecuteScalarAsync(cancellationToken) as string;
        }

        if (manifestJson is null)
        {
            return [];
        }

        var manifest = JsonSerializer.Deserialize<Domain.Apps.AppManifest>(manifestJson, Apps.AppHostService.JsonOptions);
        return (manifest?.SubscribedEvents ?? [])
            .Where(s => Apps.AppHostService.MatchesEventSpec(s, eventName))
            .Select(s => s.Handler ?? string.Empty)
            .Distinct()
            .ToList();
    }

    private static async Task<List<EventDeliveryEntry>> ListDeliveriesAsync(
        DbConnection connection,
        Guid? eventId,
        string? status,
        CancellationToken cancellationToken)
    {
        await using var cmd = TenantDb.Create(
            connection,
            null,
            """
            SELECT id, event_id, subscriber_type, subscriber_key, status, attempts, next_attempt_at, last_error, delivered_at, is_replay
              FROM core.event_delivery
             WHERE (@eventId::uuid IS NULL OR event_id = @eventId)
               AND (@status::text IS NULL OR status = @status)
             ORDER BY seq
             LIMIT 500;
            """);
        TenantDb.Add(cmd, "eventId", NpgsqlDbType.Uuid, eventId);
        TenantDb.Add(cmd, "status", NpgsqlDbType.Text, status);

        var result = new List<EventDeliveryEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new EventDeliveryEntry(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5),
                reader.GetFieldValue<DateTimeOffset>(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
                reader.GetBoolean(9)));
        }

        return result;
    }

    private static EventLogEntry ReadEvent(DbDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetGuid(3),
        reader.GetGuid(4),
        reader.GetString(5),
        reader.GetFieldValue<DateTimeOffset>(6),
        JsonNode.Parse(reader.GetString(7)));
}
