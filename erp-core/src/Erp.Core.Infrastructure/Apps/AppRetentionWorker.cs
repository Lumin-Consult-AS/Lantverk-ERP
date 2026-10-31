using Erp.Core.Domain.Tenancy;
using Erp.Core.Infrastructure.Events;
using Erp.Core.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Erp.Core.Infrastructure.Apps;

/// <summary>
/// Purges data whose grace period after an app was uninstalled (or a field was removed) has ended. Tenants with
/// anything due are found across tenants through <c>core.tenants_with_expired_retention</c>; each is then handled under
/// its own RLS context.
/// </summary>
public sealed class AppRetentionWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<AppRetentionWorker> logger) : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            do
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "App retention worker iteration failed.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>Entries older than the configured retention are removed from the delta-sync change log.</summary>
    private async Task PurgeChangeLogAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CoreDbContext>();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        var retention = scope.ServiceProvider.GetRequiredService<Erp.Core.Infrastructure.Sync.SyncOptions>().ChangeLogRetention;
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        await using var cmd = TenantDb.Create(connection, null, "SELECT core.purge_change_log(@older);");
        TenantDb.Add(cmd, "older", NpgsqlTypes.NpgsqlDbType.Interval, retention);
        var purged = Convert.ToInt64(await cmd.ExecuteScalarAsync(cancellationToken));
        if (purged > 0)
        {
            logger.LogInformation("Purged {Count} change-log entries older than {Retention}.", purged, retention);
        }
    }

    /// <summary>Purges what is due in every tenant. Returns the combined result.</summary>
    public async Task<RetentionSweepResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        var tenants = new List<Guid>();
        using (var scope = scopeFactory.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<CoreDbContext>();
            var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
            var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
            await using var cmd = TenantDb.Create(connection, null, "SELECT tenant_id FROM core.tenants_with_expired_retention();");
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                tenants.Add(reader.GetGuid(0));
            }
        }

        await PurgeChangeLogAsync(cancellationToken);

        int archives = 0, dropped = 0, cleared = 0;
        foreach (var tenantId in tenants)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId);
                var result = await scope.ServiceProvider.GetRequiredService<AppRetentionService>().PurgeExpiredAsync(cancellationToken);
                archives += result.ArchivesPurged;
                dropped += result.ColumnsDropped;
                cleared += result.ColumnsCleared;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Retention purge for tenant {TenantId} failed.", tenantId);
            }
        }

        return new RetentionSweepResult(archives, dropped, cleared);
    }
}
