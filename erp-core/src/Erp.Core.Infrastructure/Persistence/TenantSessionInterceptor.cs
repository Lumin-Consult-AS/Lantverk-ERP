using System.Data.Common;
using Erp.Core.Domain.Tenancy;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Erp.Core.Infrastructure.Persistence;

/// <summary>
/// Ensures that the PostgreSQL session variable <c>app.tenant_id</c> is set before every
/// database command according to the current <see cref="ITenantContext"/>, and cleared when
/// the connection closes.
/// </summary>
public sealed class TenantSessionInterceptor(ITenantContext tenantContext) : DbCommandInterceptor, IDbConnectionInterceptor
{
    private const string SetConfigMarker = "/* erp:set_tenant_context */";

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        ApplyTenantSessionSetting(command);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        await ApplyTenantSessionSettingAsync(command, cancellationToken);
        return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
    {
        ApplyTenantSessionSetting(command);
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await ApplyTenantSessionSettingAsync(command, cancellationToken);
        return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result)
    {
        ApplyTenantSessionSetting(command);
        return base.ScalarExecuting(command, eventData, result);
    }

    public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        await ApplyTenantSessionSettingAsync(command, cancellationToken);
        return await base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    public InterceptionResult ConnectionClosing(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        ResetTenantSessionSetting(connection);
        return result;
    }

    public async ValueTask<InterceptionResult> ConnectionClosingAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        await ResetTenantSessionSettingAsync(connection);
        return result;
    }

    private void ApplyTenantSessionSetting(DbCommand targetCommand)
    {
        if (targetCommand.CommandText.StartsWith(SetConfigMarker, StringComparison.Ordinal) || targetCommand.Connection is null)
        {
            return;
        }

        using var cmd = CreateSetConfigCommand(targetCommand.Connection, targetCommand.Transaction);
        cmd.ExecuteNonQuery();
    }

    private async Task ApplyTenantSessionSettingAsync(DbCommand targetCommand, CancellationToken cancellationToken)
    {
        if (targetCommand.CommandText.StartsWith(SetConfigMarker, StringComparison.Ordinal) || targetCommand.Connection is null)
        {
            return;
        }

        await using var cmd = CreateSetConfigCommand(targetCommand.Connection, targetCommand.Transaction);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private DbCommand CreateSetConfigCommand(DbConnection connection, DbTransaction? transaction)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"{SetConfigMarker} SELECT set_config('app.tenant_id', @tenantId, false);";

        var parameter = cmd.CreateParameter();
        parameter.ParameterName = "tenantId";
        parameter.Value = tenantContext.TenantId?.ToString("D") ?? string.Empty;
        cmd.Parameters.Add(parameter);

        return cmd;
    }

    private static void ResetTenantSessionSetting(DbConnection connection)
    {
        if (connection.State != System.Data.ConnectionState.Open)
        {
            return;
        }

        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"{SetConfigMarker} SELECT set_config('app.tenant_id', '', false);";
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // Ignore errors if the connection is broken or inside an aborted transaction.
        }
    }

    private static async Task ResetTenantSessionSettingAsync(DbConnection connection)
    {
        if (connection.State != System.Data.ConnectionState.Open)
        {
            return;
        }

        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"{SetConfigMarker} SELECT set_config('app.tenant_id', '', false);";
            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // Ignore errors if the connection is broken or inside an aborted transaction.
        }
    }
}

