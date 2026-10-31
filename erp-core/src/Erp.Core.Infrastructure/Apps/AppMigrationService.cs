using System.Data.Common;
using Erp.Core.Domain.Apps;
using Erp.Core.Domain.Tenancy;
using Erp.Core.Infrastructure.Events;
using Erp.Core.Infrastructure.Persistence;
using NpgsqlTypes;

namespace Erp.Core.Infrastructure.Apps;

public sealed class AppLifecycleOptions
{
    public const string SectionName = "Apps";

    /// <summary>
    /// How long data is kept after an app is uninstalled (or a field/object is removed by an upgrade) before it is
    /// purged for good. Within this period reinstalling the app restores it.
    /// </summary>
    public int ArchiveRetentionDays { get; set; } = 30;

    /// <summary>Safety valve against a migration that never reports completion.</summary>
    public int MaxMigrationSteps { get; set; } = 100_000;
}

public sealed record MigrationRunInfo(
    Guid Id,
    string AppId,
    string FromVersion,
    string ToVersion,
    string MigrationClass,
    string Status,
    int Steps,
    long RecordsProcessed,
    string? Checkpoint,
    int Attempts,
    string? Error,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt);

/// <summary>A data migration failed. The progress is kept; running the upgrade again resumes from the last checkpoint.</summary>
public sealed class AppMigrationException(string message, MigrationRunInfo run) : Exception(message)
{
    public MigrationRunInfo Run { get; } = run;
}

/// <summary>
/// Runs the data migrations of an upgrade for the current tenant (ADR-0013). A migration is an app method that works
/// through the data in batches; the core calls it step by step and stores the checkpoint after every step in
/// <c>core.app_migration_run</c>. Upgrading again after a failure continues from that checkpoint, and migrations that
/// already completed are not run twice.
/// </summary>
public sealed class AppMigrationService(
    CoreDbContext dbContext,
    ITenantContext tenantContext,
    AppHostService appHost,
    AppLifecycleOptions options)
{
    private const string RunColumns =
        "id, app_id, from_version, to_version, migration_class, status, steps, records_processed, checkpoint, attempts, error, started_at, finished_at";

    public async Task<IReadOnlyList<MigrationRunInfo>> RunAsync(
        AppManifest next,
        string installedVersion,
        CancellationToken cancellationToken)
    {
        var results = new List<MigrationRunInfo>();
        foreach (var migration in MigrationPlanner.Select(next, installedVersion))
        {
            results.Add(await RunOneAsync(next, migration, cancellationToken));
        }

        return results;
    }

    public async Task<IReadOnlyList<MigrationRunInfo>> ListAsync(string? appId, CancellationToken cancellationToken)
    {
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        await using var cmd = TenantDb.Create(
            connection,
            null,
            $"""
            SELECT {RunColumns} FROM core.app_migration_run
             WHERE (@app::text IS NULL OR app_id = @app) ORDER BY started_at, to_version;
            """);
        TenantDb.Add(cmd, "app", NpgsqlDbType.Text, string.IsNullOrWhiteSpace(appId) ? null : appId);
        var result = new List<MigrationRunInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(Read(reader));
        }

        return result;
    }

    private async Task<MigrationRunInfo> RunOneAsync(AppManifest next, MigrationDefinition migration, CancellationToken cancellationToken)
    {
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        var run = await StartOrResumeAsync(connection, next.Id, migration, cancellationToken);
        if (run.Status == "completed")
        {
            return run;
        }

        while (run.Steps < options.MaxMigrationSteps)
        {
            Erp.Sdk.Contracts.V1.MigrationRunResponse response;
            try
            {
                response = await appHost.RunMigrationStepAsync(
                    next.Id, next.Version, migration.From, migration.To, migration.Run, run.Checkpoint, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var reason = ex is Grpc.Core.RpcException rpc ? rpc.Status.Detail : ex.Message;
                var failed = await RecordFailureAsync(connection, run.Id, reason, cancellationToken);
                throw new AppMigrationException(
                    $"Migration '{migration.Run}' ({migration.From} -> {migration.To}) failed after {failed.RecordsProcessed} record(s): {reason} "
                    + "The progress is saved; run the upgrade again to continue from the last checkpoint.",
                    failed);
            }

            run = await RecordStepAsync(
                connection,
                run.Id,
                response.RecordsProcessed,
                string.IsNullOrEmpty(response.Checkpoint) ? null : response.Checkpoint,
                response.Completed,
                cancellationToken);

            if (response.Completed)
            {
                return run;
            }
        }

        var stuck = await RecordFailureAsync(
            connection, run.Id, $"The migration did not complete within {options.MaxMigrationSteps} steps.", cancellationToken);
        throw new AppMigrationException(stuck.Error!, stuck);
    }

    private async Task<MigrationRunInfo> StartOrResumeAsync(
        DbConnection connection,
        string appId,
        MigrationDefinition migration,
        CancellationToken cancellationToken)
    {
        var tenantId = tenantContext.TenantId ?? throw new InvalidOperationException("Migrations require a tenant context.");
        var userId = tenantContext.UserId ?? tenantId;

        await using var cmd = TenantDb.Create(
            connection,
            null,
            $"""
            INSERT INTO core.app_migration_run
                (id, tenant_id, created_by, updated_by, app_id, from_version, to_version, migration_class)
            VALUES (@id, @tenant, @user, @user, @app, @from, @to, @class)
            ON CONFLICT (tenant_id, app_id, from_version, to_version, migration_class) DO UPDATE
               SET status = CASE WHEN core.app_migration_run.status = 'completed' THEN 'completed' ELSE 'running' END,
                   attempts = CASE WHEN core.app_migration_run.status = 'completed' THEN core.app_migration_run.attempts
                                   ELSE core.app_migration_run.attempts + 1 END,
                   error = CASE WHEN core.app_migration_run.status = 'completed' THEN core.app_migration_run.error ELSE NULL END,
                   updated_at = now(), row_version = core.app_migration_run.row_version + 1
            RETURNING {RunColumns};
            """);
        TenantDb.Add(cmd, "id", NpgsqlDbType.Uuid, Guid.NewGuid());
        TenantDb.Add(cmd, "tenant", NpgsqlDbType.Uuid, tenantId);
        TenantDb.Add(cmd, "user", NpgsqlDbType.Uuid, userId);
        TenantDb.Add(cmd, "app", NpgsqlDbType.Varchar, appId);
        TenantDb.Add(cmd, "from", NpgsqlDbType.Varchar, migration.From);
        TenantDb.Add(cmd, "to", NpgsqlDbType.Varchar, migration.To);
        TenantDb.Add(cmd, "class", NpgsqlDbType.Varchar, migration.Run);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return Read(reader);
    }

    private static async Task<MigrationRunInfo> RecordStepAsync(
        DbConnection connection,
        Guid runId,
        int records,
        string? checkpoint,
        bool completed,
        CancellationToken cancellationToken)
    {
        await using var cmd = TenantDb.Create(
            connection,
            null,
            $"""
            UPDATE core.app_migration_run
               SET steps = steps + 1, records_processed = records_processed + @records, checkpoint = @checkpoint,
                   status = CASE WHEN @completed THEN 'completed' ELSE 'running' END,
                   finished_at = CASE WHEN @completed THEN now() ELSE NULL END,
                   updated_at = now(), row_version = row_version + 1
             WHERE id = @id
            RETURNING {RunColumns};
            """);
        TenantDb.Add(cmd, "id", NpgsqlDbType.Uuid, runId);
        TenantDb.Add(cmd, "records", NpgsqlDbType.Bigint, (long)records);
        TenantDb.Add(cmd, "checkpoint", NpgsqlDbType.Text, checkpoint);
        TenantDb.Add(cmd, "completed", NpgsqlDbType.Boolean, completed);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return Read(reader);
    }

    private static async Task<MigrationRunInfo> RecordFailureAsync(
        DbConnection connection,
        Guid runId,
        string error,
        CancellationToken cancellationToken)
    {
        await using var cmd = TenantDb.Create(
            connection,
            null,
            $"""
            UPDATE core.app_migration_run
               SET status = 'failed', error = @error, finished_at = now(), updated_at = now(), row_version = row_version + 1
             WHERE id = @id
            RETURNING {RunColumns};
            """);
        TenantDb.Add(cmd, "id", NpgsqlDbType.Uuid, runId);
        TenantDb.Add(cmd, "error", NpgsqlDbType.Text, error.Length > 4000 ? error[..4000] : error);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return Read(reader);
    }

    private static MigrationRunInfo Read(DbDataReader r) => new(
        r.GetGuid(0),
        r.GetString(1),
        r.GetString(2),
        r.GetString(3),
        r.GetString(4),
        r.GetString(5),
        r.GetInt32(6),
        r.GetInt64(7),
        r.IsDBNull(8) ? null : r.GetString(8),
        r.GetInt32(9),
        r.IsDBNull(10) ? null : r.GetString(10),
        r.GetFieldValue<DateTimeOffset>(11),
        r.IsDBNull(12) ? null : r.GetFieldValue<DateTimeOffset>(12));
}
