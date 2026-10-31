using Erp.Core.Domain.Tenancy;
using Erp.Core.Infrastructure.Events;
using Erp.Core.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Erp.Core.Infrastructure.Scheduling;

/// <summary>
/// Runs the schedules that app manifests declare (ADR-0025). Tenants with scheduled apps are found across tenants
/// through <c>core.tenants_with_schedules</c>; each is then handled under its own context. Scheduled commands run with
/// the tenant's administrator role, since there is no person behind them, and the core never looks inside them.
/// </summary>
public sealed class ScheduleWorker(
    IServiceScopeFactory scopeFactory,
    ScheduleOptions options,
    ILogger<ScheduleWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.PollInterval);
        try
        {
            do
            {
                try
                {
                    await RunOnceAsync(DateTimeOffset.UtcNow, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Schedule worker iteration failed.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>Runs what is due at <paramref name="now"/> in every tenant. Returns what was run.</summary>
    public async Task<IReadOnlyList<ScheduleRunResult>> RunOnceAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var tenants = new List<Guid>();
        using (var scope = scopeFactory.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<CoreDbContext>();
            var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
            var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
            await using var cmd = TenantDb.Create(connection, null, "SELECT tenant_id FROM core.tenants_with_schedules();");
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                tenants.Add(reader.GetGuid(0));
            }
        }

        var results = new List<ScheduleRunResult>();
        foreach (var tenantId in tenants)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId, tenantId, null, ["tenant.admin"]);
                results.AddRange(await scope.ServiceProvider.GetRequiredService<ScheduleService>().RunDueAsync(now, cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Running schedules for tenant {TenantId} failed.", tenantId);
            }
        }

        return results;
    }
}
