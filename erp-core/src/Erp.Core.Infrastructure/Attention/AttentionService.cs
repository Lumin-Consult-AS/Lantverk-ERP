using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Erp.Core.Domain.Apps;
using Erp.Core.Domain.Data;
using Erp.Core.Domain.Tenancy;
using Erp.Core.Infrastructure.Data;
using Erp.Core.Infrastructure.Documents;
using Erp.Core.Infrastructure.Events;
using Erp.Core.Infrastructure.Persistence;
using Erp.Core.Infrastructure.Security;
using Erp.Core.Infrastructure.Workflow;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;

namespace Erp.Core.Infrastructure.Attention;

public sealed record AttentionItem(
    string Id,
    string Label,
    string Severity,
    int Count,
    string? OwnerApp,
    string? Page,
    Dictionary<string, object?>? Filter,
    bool Hidden,
    DateTimeOffset? SnoozedUntil);

/// <summary>
/// Per-user counts of attention items. Counts are cached for a short time and dropped for the whole tenant whenever
/// an event is committed, so they follow the data without recounting on every request.
/// </summary>
public sealed class AttentionCache
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<(Guid Tenant, Guid User, string Id), (int Count, DateTimeOffset At)> _counts = new();

    public bool TryGet(Guid tenantId, Guid userId, string id, out int count)
    {
        if (_counts.TryGetValue((tenantId, userId, id), out var entry) && DateTimeOffset.UtcNow - entry.At < Ttl)
        {
            count = entry.Count;
            return true;
        }

        count = 0;
        return false;
    }

    public void Set(Guid tenantId, Guid userId, string id, int count) =>
        _counts[(tenantId, userId, id)] = (count, DateTimeOffset.UtcNow);

    public void InvalidateTenant(Guid tenantId)
    {
        foreach (var key in _counts.Keys.Where(k => k.Tenant == tenantId))
        {
            _counts.TryRemove(key, out _);
        }
    }
}

/// <summary>
/// Builds the exception-driven start page. Sources are the core's own open tasks ("Til godkjenning") and the
/// declarative <c>attention</c> queries of installed apps. Items with nothing to show are left out; a user can
/// hide an item or snooze it until a given time.
/// </summary>
public sealed class AttentionService(
    CoreDbContext dbContext,
    ITenantContext tenantContext,
    WorkflowEngine workflowEngine,
    InboxService inbox,
    RecordDataService recordData,
    PermissionService permissions,
    AttentionCache cache)
{
    public const string OpenTasksId = "core.task.mine";
    public const string InboxNewId = "core.inbox.new";

    public async Task<IReadOnlyList<AttentionItem>> GetAsync(CancellationToken cancellationToken)
    {
        var tenantId = tenantContext.TenantId ?? throw new InvalidOperationException("Attention requires a tenant context.");
        var userId = tenantContext.UserId ?? throw new UnauthorizedAccessException("Attention requires a user.");

        var dismissals = await LoadDismissalsAsync(cancellationToken);
        var items = new List<AttentionItem>();

        var taskCount = await CountCachedAsync(tenantId, userId, OpenTasksId, () => workflowEngine.CountMyOpenTasksAsync(cancellationToken));
        if (taskCount > 0)
        {
            items.Add(Build(OpenTasksId, "Til godkjenning", "action", taskCount, null, "core.tasks", null, dismissals));
        }

        if (await permissions.IsGrantedAsync("core.inboxitem", "read", cancellationToken))
        {
            var newItems = await CountCachedAsync(tenantId, userId, InboxNewId, () => inbox.CountNewAsync(cancellationToken));
            if (newItems > 0)
            {
                items.Add(Build(InboxNewId, "Nye dokumenter i innboksen", "warning", newItems, null, "core.inbox", null, dismissals));
            }
        }

        var apps = await dbContext.InstalledApps.AsNoTracking().OrderBy(a => a.CreatedAt).ToListAsync(cancellationToken);
        foreach (var app in apps)
        {
            var manifest = JsonSerializer.Deserialize<AppManifest>(app.ManifestJson, Apps.AppHostService.JsonOptions);
            foreach (var attention in manifest?.AttentionItems ?? [])
            {
                if (!await IsAudienceAsync(attention, cancellationToken))
                {
                    continue;
                }

                var count = await CountSourceAsync(tenantId, userId, attention, cancellationToken);
                if (count > 0)
                {
                    var page = attention.Action is not null && attention.Action.TryGetValue("page", out var p) ? p?.ToString() : null;
                    items.Add(Build(attention.Id, attention.Label, attention.Severity, count, attention.OwnerApp, page, null, dismissals));
                }
            }
        }

        return items
            .OrderBy(i => SeverityRank(i.Severity))
            .ThenBy(i => i.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public Task HideAsync(string attentionId, CancellationToken cancellationToken) =>
        SetDismissalAsync(attentionId, snoozedUntil: null, cancellationToken);

    public Task SnoozeAsync(string attentionId, DateTimeOffset until, CancellationToken cancellationToken) =>
        SetDismissalAsync(attentionId, until, cancellationToken);

    public async Task RestoreAsync(string attentionId, CancellationToken cancellationToken)
    {
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        await using var cmd = TenantDb.Create(
            connection, null, "DELETE FROM core.attention_dismissal WHERE user_id = @user AND attention_id = @id;");
        TenantDb.Add(cmd, "user", NpgsqlDbType.Uuid, tenantContext.UserId);
        TenantDb.Add(cmd, "id", NpgsqlDbType.Varchar, attentionId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task SetDismissalAsync(string attentionId, DateTimeOffset? snoozedUntil, CancellationToken cancellationToken)
    {
        var tenantId = tenantContext.TenantId ?? throw new InvalidOperationException("Attention requires a tenant context.");
        var userId = tenantContext.UserId ?? throw new UnauthorizedAccessException("Attention requires a user.");

        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        await using var cmd = TenantDb.Create(
            connection,
            null,
            """
            INSERT INTO core.attention_dismissal (id, tenant_id, created_by, updated_by, user_id, attention_id, snoozed_until)
            VALUES (@rowId, @tenant, @user, @user, @user, @id, @until)
            ON CONFLICT (tenant_id, user_id, attention_id) DO UPDATE
               SET snoozed_until = EXCLUDED.snoozed_until, updated_at = now(), row_version = core.attention_dismissal.row_version + 1;
            """);
        TenantDb.Add(cmd, "rowId", NpgsqlDbType.Uuid, Guid.NewGuid());
        TenantDb.Add(cmd, "tenant", NpgsqlDbType.Uuid, tenantId);
        TenantDb.Add(cmd, "user", NpgsqlDbType.Uuid, userId);
        TenantDb.Add(cmd, "id", NpgsqlDbType.Varchar, attentionId);
        TenantDb.Add(cmd, "until", NpgsqlDbType.TimestampTz, snoozedUntil);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<Dictionary<string, DateTimeOffset?>> LoadDismissalsAsync(CancellationToken cancellationToken)
    {
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        await using var cmd = TenantDb.Create(
            connection, null, "SELECT attention_id, snoozed_until FROM core.attention_dismissal WHERE user_id = @user;");
        TenantDb.Add(cmd, "user", NpgsqlDbType.Uuid, tenantContext.UserId);

        var result = new Dictionary<string, DateTimeOffset?>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1);
        }

        return result;
    }

    private static AttentionItem Build(
        string id,
        string label,
        string severity,
        int count,
        string? ownerApp,
        string? page,
        Dictionary<string, object?>? filter,
        Dictionary<string, DateTimeOffset?> dismissals)
    {
        var hidden = false;
        DateTimeOffset? snoozedUntil = null;
        if (dismissals.TryGetValue(id, out var until))
        {
            snoozedUntil = until;
            hidden = until is null || until > DateTimeOffset.UtcNow;
        }

        return new AttentionItem(id, label, severity, count, ownerApp, page, filter, hidden, snoozedUntil);
    }

    private async Task<int> CountSourceAsync(Guid tenantId, Guid userId, AttentionDefinition attention, CancellationToken cancellationToken)
    {
        if (!attention.Source.TryGetValue("query", out var queryObj) || queryObj?.ToString() is not { Length: > 0 } objectId)
        {
            return 0; // Hook-based sources (OnAttention) are not supported yet.
        }

        return await CountCachedAsync(tenantId, userId, attention.Id, async () =>
        {
            var filter = attention.Source.TryGetValue("filter", out var f) ? BuildFilter(f, userId) : null;
            try
            {
                return await recordData.CountAsync(objectId, filter, tenantContext.CompanyId, cancellationToken);
            }
            catch (Exception ex) when (ex is ObjectNotFoundException or RecordValidationException or PermissionDeniedException)
            {
                return 0; // The source object or a field is unavailable, or the user may not read it.
            }
        });
    }

    private async Task<int> CountCachedAsync(Guid tenantId, Guid userId, string id, Func<Task<int>> count)
    {
        if (cache.TryGet(tenantId, userId, id, out var cached))
        {
            return cached;
        }

        var value = await count();
        cache.Set(tenantId, userId, id, value);
        return value;
    }

    /// <summary>
    /// Audience by permission: <c>audience.permission</c> is a permission set id or an <c>object.action</c> the user
    /// must hold through their roles. No audience means everyone.
    /// </summary>
    private async Task<bool> IsAudienceAsync(AttentionDefinition attention, CancellationToken cancellationToken)
    {
        if (attention.Audience is null || !attention.Audience.TryGetValue("permission", out var required) || required is null)
        {
            return true;
        }

        return await permissions.HoldsAsync(required.ToString()!, cancellationToken);
    }

    /// <summary>Builds a filter from the manifest form <c>{ field: value }</c> or <c>{ field: { op: value } }</c>.</summary>
    internal static FilterNode? BuildFilter(object? source, Guid userId)
    {
        if (Normalize(source) is not IDictionary<string, object?> map || map.Count == 0)
        {
            return null;
        }

        var clauses = new List<FilterNode>();
        foreach (var (field, raw) in map)
        {
            if (Normalize(raw) is IDictionary<string, object?> ops)
            {
                foreach (var (op, value) in ops)
                {
                    clauses.Add(new FilterCondition(field, ParseOperator(op), Substitute(value, userId)));
                }
            }
            else
            {
                clauses.Add(new FilterCondition(field, FilterOperator.Eq, Substitute(raw, userId)));
            }
        }

        return new FilterGroup(LogicalOperator.And, clauses);
    }

    /// <summary>The stored manifest is JSON, so nested values arrive as <see cref="JsonElement"/>; turn them into plain objects.</summary>
    private static object? Normalize(object? value)
    {
        if (value is not JsonElement e)
        {
            return value;
        }

        return e.ValueKind switch
        {
            JsonValueKind.Object => e.EnumerateObject().ToDictionary(p => p.Name, p => Normalize(p.Value)),
            JsonValueKind.Array => e.EnumerateArray().Select(x => Normalize(x)).ToList(),
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number => e.GetDecimal(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static object? Substitute(object? value, Guid userId) => value switch
    {
        string s when s == "{me}" => userId.ToString("D"),
        string s when s == "{today}" => DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        string s when decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) => d,
        _ => value
    };

    private static FilterOperator ParseOperator(string op) => op.ToLowerInvariant() switch
    {
        "eq" => FilterOperator.Eq,
        "ne" => FilterOperator.Ne,
        "gt" => FilterOperator.Gt,
        "ge" => FilterOperator.Ge,
        "lt" => FilterOperator.Lt,
        "le" => FilterOperator.Le,
        "in" => FilterOperator.In,
        "contains" => FilterOperator.Contains,
        "isempty" => FilterOperator.IsEmpty,
        _ => throw new RecordValidationException($"Unsupported attention filter operator '{op}'.")
    };

    private static int SeverityRank(string severity) => severity.ToLowerInvariant() switch
    {
        "action" => 0,
        "warning" => 1,
        _ => 2
    };
}
