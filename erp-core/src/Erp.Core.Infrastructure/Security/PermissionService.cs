using System.Text.Json;
using System.Text.Json.Serialization;
using Erp.Core.Domain.Apps;
using Erp.Core.Domain.Data;
using Erp.Core.Domain.Tenancy;
using Erp.Core.Infrastructure.Events;
using Erp.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;

namespace Erp.Core.Infrastructure.Security;

public sealed record EffectivePermission(string ObjectId, IReadOnlyList<string> Actions);

public sealed record RoleInfo(string Role, IReadOnlyList<string> PermissionSets);

/// <summary>
/// Evaluates what the current user may do. Permission sets are defined by installed apps
/// (<c>permissionSets</c>: object → actions); a tenant composes roles from them (<c>core.role_permission_set</c>)
/// and users carry role names. <c>tenant.admin</c> holds everything. Without a matching role nothing is granted
/// (fail closed). Checks are made in the core's data API, never in the UI or in apps (ADR-0011).
/// </summary>
public sealed class PermissionService(CoreDbContext dbContext, ITenantContext tenantContext)
{
    public const string AdminRole = "tenant.admin";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private Dictionary<string, HashSet<string>>? _grants;
    private HashSet<string>? _heldSets;

    public bool IsAdmin => tenantContext.Roles.Contains(AdminRole, StringComparer.OrdinalIgnoreCase);

    public async Task<bool> IsGrantedAsync(string objectId, string action, CancellationToken cancellationToken)
    {
        if (IsAdmin)
        {
            return true;
        }

        await EnsureLoadedAsync(cancellationToken);
        return _grants!.TryGetValue(objectId, out var actions) && actions.Contains(action);
    }

    public async Task RequireAsync(string objectId, string action, CancellationToken cancellationToken)
    {
        if (!await IsGrantedAsync(objectId, action, cancellationToken))
        {
            throw new PermissionDeniedException($"Permission '{action}' on '{objectId}' is required.");
        }
    }

    /// <summary>
    /// True when the user holds the permission: either a permission set id (<c>core.parties.reader</c>) or an
    /// object action written <c>object.action</c> (<c>core.party.read</c>).
    /// </summary>
    public async Task<bool> HoldsAsync(string permission, CancellationToken cancellationToken)
    {
        if (IsAdmin)
        {
            return true;
        }

        await EnsureLoadedAsync(cancellationToken);
        if (_heldSets!.Contains(permission))
        {
            return true;
        }

        var split = permission.LastIndexOf('.');
        return split > 0 && await IsGrantedAsync(permission[..split], permission[(split + 1)..], cancellationToken);
    }

    public async Task<IReadOnlyList<EffectivePermission>> GetEffectiveAsync(CancellationToken cancellationToken)
    {
        if (IsAdmin)
        {
            return (await LoadPermissionSetsAsync(cancellationToken))
                .SelectMany(s => s.Grants)
                .GroupBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => new EffectivePermission(g.Key, g.SelectMany(x => x.Value).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList()))
                .OrderBy(p => p.ObjectId)
                .ToList();
        }

        await EnsureLoadedAsync(cancellationToken);
        return _grants!
            .Select(g => new EffectivePermission(g.Key, g.Value.Order().ToList()))
            .OrderBy(p => p.ObjectId)
            .ToList();
    }

    // ------------------------------------------------------------------ role administration

    /// <summary>Every role in use (on a user or mapped to a permission set) with the permission sets it holds.</summary>
    public async Task<IReadOnlyList<RoleInfo>> ListRolesAsync(CancellationToken cancellationToken)
    {
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        var sets = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        await using (var cmd = TenantDb.Create(
            connection, null, """SELECT DISTINCT r FROM core."user", unnest(roles) AS r;"""))
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                sets.TryAdd(reader.GetString(0), []);
            }
        }

        await using (var cmd = TenantDb.Create(
            connection, null, "SELECT role, permission_set_id FROM core.role_permission_set ORDER BY role, permission_set_id;"))
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!sets.TryGetValue(reader.GetString(0), out var list))
                {
                    sets[reader.GetString(0)] = list = [];
                }

                list.Add(reader.GetString(1));
            }
        }

        return sets.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new RoleInfo(kv.Key, kv.Value))
            .ToList();
    }

    /// <summary>Replaces the permission sets a role holds. <c>tenant.admin</c> is implicit and cannot be edited.</summary>
    public async Task SetRolePermissionSetsAsync(
        string role,
        IReadOnlyCollection<string> permissionSetIds,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            throw new RecordValidationException("Role name is required.", "role");
        }

        if (string.Equals(role, AdminRole, StringComparison.OrdinalIgnoreCase))
        {
            throw new RecordValidationException($"Role '{AdminRole}' holds every permission and cannot be edited.", "role");
        }

        var known = (await LoadPermissionSetsAsync(cancellationToken)).Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = permissionSetIds.Where(id => !known.Contains(id)).ToList();
        if (unknown.Count > 0)
        {
            throw new RecordValidationException($"Unknown permission set(s): {string.Join(", ", unknown)}.", "permissionSets");
        }

        var tenantId = tenantContext.TenantId
            ?? throw new InvalidOperationException("Role administration requires a tenant context.");
        var userId = tenantContext.UserId ?? tenantId;
        var wanted = permissionSetIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        await using (var delete = TenantDb.Create(
            connection, tx, "DELETE FROM core.role_permission_set WHERE role = @role AND NOT (permission_set_id = ANY (@keep));"))
        {
            TenantDb.Add(delete, "role", NpgsqlDbType.Varchar, role);
            TenantDb.Add(delete, "keep", NpgsqlDbType.Array | NpgsqlDbType.Text, wanted);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var id in wanted)
        {
            await using var insert = TenantDb.Create(
                connection,
                tx,
                """
                INSERT INTO core.role_permission_set (id, tenant_id, created_by, updated_by, role, permission_set_id)
                VALUES (@id, @tenant, @user, @user, @role, @set)
                ON CONFLICT (tenant_id, role, permission_set_id) DO NOTHING;
                """);
            TenantDb.Add(insert, "id", NpgsqlDbType.Uuid, Guid.NewGuid());
            TenantDb.Add(insert, "tenant", NpgsqlDbType.Uuid, tenantId);
            TenantDb.Add(insert, "user", NpgsqlDbType.Uuid, userId);
            TenantDb.Add(insert, "role", NpgsqlDbType.Varchar, role);
            TenantDb.Add(insert, "set", NpgsqlDbType.Varchar, id);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        _grants = null;
        _heldSets = null;
    }

    // ------------------------------------------------------------------ internals

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_grants is not null)
        {
            return;
        }

        var held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roles = tenantContext.Roles.ToArray();
        if (roles.Length > 0)
        {
            var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
            await using var cmd = TenantDb.Create(
                connection, null, "SELECT DISTINCT permission_set_id FROM core.role_permission_set WHERE role = ANY (@roles);");
            TenantDb.Add(cmd, "roles", NpgsqlDbType.Array | NpgsqlDbType.Text, roles);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                held.Add(reader.GetString(0));
            }
        }

        var grants = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var set in await LoadPermissionSetsAsync(cancellationToken))
        {
            if (!held.Contains(set.Id))
            {
                continue;
            }

            foreach (var (objectId, actions) in set.Grants)
            {
                if (!grants.TryGetValue(objectId, out var existing))
                {
                    grants[objectId] = existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }

                existing.UnionWith(actions);
            }
        }

        _heldSets = held;
        _grants = grants;
    }

    private async Task<List<PermissionSetDefinition>> LoadPermissionSetsAsync(CancellationToken cancellationToken)
    {
        var json = await dbContext.InstalledApps
            .AsNoTracking()
            .Select(a => a.PermissionSetsJson)
            .ToListAsync(cancellationToken);

        return CorePermissionSets.All
            .Concat(json.SelectMany(j => JsonSerializer.Deserialize<List<PermissionSetDefinition>>(j, JsonOptions) ?? []))
            .ToList();
    }
}

/// <summary>
/// Permission sets owned by the core itself (documents), available in every tenant without installing an app.
/// They are granted to roles like any other set (ADR-0011, ADR-0012).
/// </summary>
public static class CorePermissionSets
{
    private static readonly string[] Documents = ["core.file", "core.inbox", "core.inboxitem", "core.attachment"];

    public static IReadOnlyList<PermissionSetDefinition> All { get; } =
    [
        new()
        {
            Id = "core.documents.reader",
            OwnerApp = "core",
            Label = "Dokumenter – lesetilgang",
            Grants = Documents.ToDictionary(o => o, _ => new List<string> { "read" }, StringComparer.OrdinalIgnoreCase)
        },
        new()
        {
            Id = "core.documents.user",
            OwnerApp = "core",
            Label = "Dokumenter – full tilgang",
            Grants = Documents.ToDictionary(
                o => o, _ => new List<string> { "read", "create", "update", "delete" }, StringComparer.OrdinalIgnoreCase)
        }
    ];
}
