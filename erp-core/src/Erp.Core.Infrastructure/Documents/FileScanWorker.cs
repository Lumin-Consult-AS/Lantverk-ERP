using Erp.Core.Domain.Tenancy;
using Erp.Core.Infrastructure.Events;
using Erp.Core.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NpgsqlTypes;

namespace Erp.Core.Infrastructure.Documents;

/// <summary>
/// Scans files that were stored while the virus scanner was unavailable, so nothing stays unavailable for good. Files
/// are claimed across tenants through <c>core.claim_pending_scans</c> and scanned under the tenant's own context;
/// inbox items waiting for them are then released (or rejected).
/// </summary>
public sealed class FileScanWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<FileScanWorker> logger) : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

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
                    logger.LogError(ex, "File scan worker iteration failed.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>Claims and scans pending files. Returns how many files left the pending state.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var claimed = new List<(Guid FileId, Guid TenantId)>();
        using (var scope = scopeFactory.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<CoreDbContext>();
            var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
            var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
            await using var cmd = TenantDb.Create(connection, null, "SELECT file_id, tenant_id FROM core.claim_pending_scans(@limit);");
            TenantDb.Add(cmd, "limit", NpgsqlDbType.Integer, 20);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                claimed.Add((reader.GetGuid(0), reader.GetGuid(1)));
            }
        }

        var resolved = 0;
        foreach (var (fileId, tenantId) in claimed)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId);
                var status = await scope.ServiceProvider.GetRequiredService<FileService>().RescanAsync(fileId, cancellationToken);
                if (status is "clean" or "infected")
                {
                    resolved++;
                    await scope.ServiceProvider.GetRequiredService<InboxService>().ReleaseScannedItemsAsync(fileId, cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Scanning file {FileId} for tenant {TenantId} failed.", fileId, tenantId);
            }
        }

        return resolved;
    }
}
