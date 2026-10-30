using System.Data;
using System.Data.Common;
using Erp.Core.Domain.Tenancy;
using Erp.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Erp.Core.Infrastructure.Events;

/// <summary>
/// Raw-SQL helpers for the event tables. Raw commands are not covered by the EF session interceptor,
/// so the tenant context (<c>app.tenant_id</c>) is set explicitly before use.
/// </summary>
internal static class TenantDb
{
    public static async Task<DbConnection> OpenAsync(
        CoreDbContext dbContext,
        ITenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "/* erp:set_tenant_context */ SELECT set_config('app.tenant_id', @tenantId, false), set_config('app.user_id', @userId, false);";
        Add(cmd, "tenantId", NpgsqlDbType.Text, tenantContext.TenantId?.ToString("D") ?? string.Empty);
        Add(cmd, "userId", NpgsqlDbType.Text, tenantContext.UserId?.ToString("D") ?? string.Empty);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    public static DbParameter Add(DbCommand cmd, string name, NpgsqlDbType type, object? value)
    {
        var p = new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value };
        cmd.Parameters.Add(p);
        return p;
    }

    public static DbCommand Create(DbConnection connection, DbTransaction? tx, string sql)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd;
    }
}
