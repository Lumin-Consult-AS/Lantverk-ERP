using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using Erp.Core.Domain.Data;
using Erp.Core.Domain.Metadata;
using Erp.Core.Domain.Tenancy;
using Erp.Core.Infrastructure.Events;
using Erp.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Erp.Core.Infrastructure.Apps;

public sealed record AppArchiveInfo(
    Guid Id,
    string AppId,
    string AppVersion,
    string Reason,
    IReadOnlyList<string> ObjectIds,
    DateTimeOffset ArchivedAt,
    DateTimeOffset PurgeAfter,
    string Status,
    DateTimeOffset? PurgedAt,
    long? RowsPurged);

public sealed record RetentionSweepResult(int ArchivesPurged, int ColumnsDropped, int ColumnsCleared);

/// <summary>
/// Data lifecycle after an app is removed (ADR-0013). Uninstalling does not delete data: the objects' rows are
/// archived (kept, but invisible) for a grace period and are restored if the app is reinstalled in time. After the
/// period the tenant's rows are deleted. Tables and columns are shared by all tenants, so a table or column is only
/// dropped when no tenant uses it any more.
/// </summary>
public sealed class AppRetentionService(
    CoreDbContext dbContext,
    ITenantContext tenantContext,
    IConfiguration configuration,
    AppLifecycleOptions options,
    ILogger<AppRetentionService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private const string ArchiveColumns =
        "id, app_id, app_version, reason, object_ids::text, archived_at, purge_after, status, purged_at, rows_purged";

    private string MigrationConnectionString =>
        configuration.GetConnectionString("MigrationConnection")
        ?? "Host=localhost;Port=5432;Database=erp;Username=erp_migrator;Password=erp_migrator_dev";

    public DateTimeOffset PurgeDate => DateTimeOffset.UtcNow.AddDays(options.ArchiveRetentionDays);

    public async Task<AppArchiveInfo?> ArchiveAsync(
        string appId,
        string appVersion,
        IReadOnlyCollection<string> objectIds,
        string reason,
        CancellationToken cancellationToken)
    {
        if (objectIds.Count == 0)
        {
            return null;
        }

        var tenantId = tenantContext.TenantId ?? throw new InvalidOperationException("Archiving requires a tenant context.");
        var userId = tenantContext.UserId ?? tenantId;
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        await using var cmd = TenantDb.Create(
            connection,
            null,
            $"""
            INSERT INTO core.app_archive (id, tenant_id, created_by, updated_by, app_id, app_version, reason, object_ids, purge_after)
            VALUES (@id, @tenant, @user, @user, @app, @version, @reason, @objects::jsonb, @purge)
            RETURNING {ArchiveColumns};
            """);
        TenantDb.Add(cmd, "id", NpgsqlDbType.Uuid, Guid.NewGuid());
        TenantDb.Add(cmd, "tenant", NpgsqlDbType.Uuid, tenantId);
        TenantDb.Add(cmd, "user", NpgsqlDbType.Uuid, userId);
        TenantDb.Add(cmd, "app", NpgsqlDbType.Varchar, appId);
        TenantDb.Add(cmd, "version", NpgsqlDbType.Varchar, appVersion);
        TenantDb.Add(cmd, "reason", NpgsqlDbType.Varchar, reason);
        TenantDb.Add(cmd, "objects", NpgsqlDbType.Text, JsonSerializer.Serialize(objectIds));
        TenantDb.Add(cmd, "purge", NpgsqlDbType.TimestampTz, PurgeDate);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return ReadArchive(reader);
    }

    /// <summary>The app is installed again: its archived data becomes live again and is no longer scheduled for purging.</summary>
    public async Task<int> RestoreAsync(string appId, CancellationToken cancellationToken)
    {
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        await using var cmd = TenantDb.Create(
            connection,
            null,
            """
            UPDATE core.app_archive SET status = 'restored', updated_at = now(), row_version = row_version + 1
             WHERE app_id = @app AND status = 'archived' AND reason = 'uninstall';
            """);
        TenantDb.Add(cmd, "app", NpgsqlDbType.Varchar, appId);
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AppArchiveInfo>> ListAsync(CancellationToken cancellationToken)
    {
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        await using var cmd = TenantDb.Create(
            connection, null, $"SELECT {ArchiveColumns} FROM core.app_archive ORDER BY archived_at DESC;");
        var result = new List<AppArchiveInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadArchive(reader));
        }

        return result;
    }

    /// <summary>Deletes the tenant's archived data now instead of waiting for the grace period to end.</summary>
    public async Task<AppArchiveInfo> PurgeAsync(Guid archiveId, CancellationToken cancellationToken)
    {
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        AppArchiveInfo archive;
        await using (var select = TenantDb.Create(connection, null, $"SELECT {ArchiveColumns} FROM core.app_archive WHERE id = @id;"))
        {
            TenantDb.Add(select, "id", NpgsqlDbType.Uuid, archiveId);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            archive = await reader.ReadAsync(cancellationToken)
                ? ReadArchive(reader)
                : throw new KeyNotFoundException($"Archive '{archiveId}' was not found.");
        }

        if (archive.Status != "archived")
        {
            throw new RecordValidationException($"Archive '{archiveId}' is {archive.Status} and cannot be purged.");
        }

        long rows = 0;
        foreach (var objectId in archive.ObjectIds)
        {
            rows += await PurgeObjectDataAsync(connection, archive.AppId, objectId, cancellationToken);
        }

        await using var update = TenantDb.Create(
            connection,
            null,
            $"""
            UPDATE core.app_archive
               SET status = 'purged', purged_at = now(), rows_purged = @rows, updated_at = now(), row_version = row_version + 1
             WHERE id = @id
            RETURNING {ArchiveColumns};
            """);
        TenantDb.Add(update, "id", NpgsqlDbType.Uuid, archiveId);
        TenantDb.Add(update, "rows", NpgsqlDbType.Bigint, rows);
        await using var updated = await update.ExecuteReaderAsync(cancellationToken);
        await updated.ReadAsync(cancellationToken);
        return ReadArchive(updated);
    }

    /// <summary>Purges everything whose grace period has passed for the current tenant: archives and orphaned columns.</summary>
    public async Task<RetentionSweepResult> PurgeExpiredAsync(CancellationToken cancellationToken)
    {
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);

        var due = new List<Guid>();
        await using (var cmd = TenantDb.Create(
            connection, null, "SELECT id FROM core.app_archive WHERE status = 'archived' AND purge_after <= now();"))
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                due.Add(reader.GetGuid(0));
            }
        }

        var purged = 0;
        foreach (var id in due)
        {
            try
            {
                await PurgeAsync(id, cancellationToken);
                purged++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Purging archive {ArchiveId} failed; it stays archived and is retried.", id);
            }
        }

        var (dropped, cleared) = await PurgeExpiredColumnsAsync(connection, cancellationToken);
        return new RetentionSweepResult(purged, dropped, cleared);
    }

    // ------------------------------------------------------------------ orphaned columns

    private async Task<(int Dropped, int Cleared)> PurgeExpiredColumnsAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var tenantId = tenantContext.TenantId!.Value;
        var dropped = 0;
        var cleared = 0;

        var items = await dbContext.MetadataItems.Where(m => m.Kind == "object").ToListAsync(cancellationToken);
        foreach (var item in items)
        {
            var definition = JsonSerializer.Deserialize<ObjectDefinition>(item.DefinitionJson, JsonOptions);
            var expired = definition?.OrphanedColumns.Where(o => o.DropAfter <= DateTimeOffset.UtcNow).ToList() ?? [];
            if (definition is null || expired.Count == 0)
            {
                continue;
            }

            var (schema, table) = definition.GetSchemaAndTable();
            foreach (var orphan in expired)
            {
                try
                {
                    // This tenant's values are deleted either way; the column itself is shared, so it only goes when
                    // no other tenant still has the field or is still within its own grace period for it.
                    await ClearColumnAsync(connection, schema, table, orphan.ColumnName, cancellationToken);
                    cleared++;

                    if (!await ColumnInUseByOtherTenantAsync(connection, definition, orphan.ColumnName, tenantId, cancellationToken))
                    {
                        await DropColumnAsync(schema, table, orphan.ColumnName, cancellationToken);
                        dropped++;
                    }

                    definition.OrphanedColumns.RemoveAll(o => string.Equals(o.ColumnName, orphan.ColumnName, StringComparison.OrdinalIgnoreCase));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Purging orphaned column {Column} on {Object} failed; it is retried.", orphan.ColumnName, definition.Id);
                }
            }

            item.DefinitionJson = JsonSerializer.Serialize(definition, JsonOptions);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return (dropped, cleared);
    }

    private static async Task ClearColumnAsync(DbConnection connection, string schema, string table, string column, CancellationToken cancellationToken)
    {
        try
        {
            await using var cmd = TenantDb.Create(
                connection, null, $"""UPDATE "{schema}"."{table}" SET "{column}" = NULL WHERE "{column}" IS NOT NULL;""");
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedColumn or PostgresErrorCodes.UndefinedTable)
        {
            // Already gone.
        }
    }

    private static async Task<bool> ColumnInUseByOtherTenantAsync(
        DbConnection connection,
        ObjectDefinition definition,
        string column,
        Guid thisTenant,
        CancellationToken cancellationToken)
    {
        await using var cmd = TenantDb.Create(connection, null, "SELECT tenant_id, definition::text FROM core.metadata_object_definitions(@object);");
        TenantDb.Add(cmd, "object", NpgsqlDbType.Text, definition.Id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.GetGuid(0) == thisTenant)
            {
                continue;
            }

            var other = JsonSerializer.Deserialize<ObjectDefinition>(reader.GetString(1), JsonOptions);
            if (other is null)
            {
                continue;
            }

            if (other.Fields.Any(f => string.Equals(f.GetColumnName(other.OwnerApp), column, StringComparison.OrdinalIgnoreCase))
                || other.OrphanedColumns.Any(o => string.Equals(o.ColumnName, column, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private async Task DropColumnAsync(string schema, string table, string column, CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(MigrationConnectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand($"""ALTER TABLE "{schema}"."{table}" DROP COLUMN IF EXISTS "{column}";""", conn);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    // ------------------------------------------------------------------ archived objects

    private async Task<long> PurgeObjectDataAsync(DbConnection connection, string appId, string objectId, CancellationToken cancellationToken)
    {
        var (schema, table) = IdentifierNaming.SplitObjectId(objectId);

        long deleted;
        try
        {
            await using var cmd = TenantDb.Create(connection, null, $"""DELETE FROM "{schema}"."{table}";""");
            deleted = await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return 0;
        }

        // The table is shared by every tenant: drop it only when no tenant has the app any more and it is empty.
        if (!await AppInstalledAnywhereAsync(connection, appId, cancellationToken))
        {
            await using var conn = new NpgsqlConnection(MigrationConnectionString);
            await conn.OpenAsync(cancellationToken);
            await using var count = new NpgsqlCommand($"""SELECT EXISTS (SELECT 1 FROM "{schema}"."{table}");""", conn);
            if (!(bool)(await count.ExecuteScalarAsync(cancellationToken))!)
            {
                foreach (var sql in new[]
                         {
                             $"""DROP TABLE IF EXISTS "{schema}"."{table}" CASCADE;""",
                             $"""DROP TABLE IF EXISTS "{schema}"."{table}_audit" CASCADE;"""
                         })
                {
                    await using var drop = new NpgsqlCommand(sql, conn);
                    await drop.ExecuteNonQueryAsync(cancellationToken);
                }

                try
                {
                    await using var dropSchema = new NpgsqlCommand($"""DROP SCHEMA IF EXISTS "{schema}";""", conn);
                    await dropSchema.ExecuteNonQueryAsync(cancellationToken);
                }
                catch (PostgresException)
                {
                    // The schema still holds other objects of the app.
                }
            }
        }

        return deleted;
    }

    private static async Task<bool> AppInstalledAnywhereAsync(DbConnection connection, string appId, CancellationToken cancellationToken)
    {
        await using var cmd = TenantDb.Create(connection, null, "SELECT core.app_installed_anywhere(@app);");
        TenantDb.Add(cmd, "app", NpgsqlDbType.Text, appId);
        return (bool)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    private static AppArchiveInfo ReadArchive(DbDataReader r) => new(
        r.GetGuid(0),
        r.GetString(1),
        r.GetString(2),
        r.GetString(3),
        JsonSerializer.Deserialize<List<string>>(r.GetString(4)) ?? [],
        r.GetFieldValue<DateTimeOffset>(5),
        r.GetFieldValue<DateTimeOffset>(6),
        r.GetString(7),
        r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8),
        r.IsDBNull(9) ? null : r.GetInt64(9));
}
