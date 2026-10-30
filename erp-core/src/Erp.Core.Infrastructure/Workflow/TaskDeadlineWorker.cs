using Erp.Core.Domain.Tenancy;
using Erp.Core.Infrastructure.Events;
using Erp.Core.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NpgsqlTypes;

namespace Erp.Core.Infrastructure.Workflow;

/// <summary>
/// Escalates open tasks whose deadline has passed. Overdue tasks are claimed across tenants through
/// <c>core.claim_overdue_tasks</c> (which marks them escalated, so each is handled once) and are then reassigned
/// under the tenant's own RLS context.
/// </summary>
public sealed class TaskDeadlineWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<TaskDeadlineWorker> logger) : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

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
                    logger.LogError(ex, "Task deadline worker iteration failed.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>Claims and escalates overdue tasks. Returns how many tasks were escalated.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var claimed = new List<(Guid TaskId, Guid TenantId)>();
        using (var scope = scopeFactory.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<CoreDbContext>();
            var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
            var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
            await using var cmd = TenantDb.Create(connection, null, "SELECT task_id, tenant_id FROM core.claim_overdue_tasks(@limit);");
            TenantDb.Add(cmd, "limit", NpgsqlDbType.Integer, 50);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                claimed.Add((reader.GetGuid(0), reader.GetGuid(1)));
            }
        }

        foreach (var (taskId, tenantId) in claimed)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId);
                await scope.ServiceProvider.GetRequiredService<WorkflowEngine>().EscalateAsync(taskId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Escalation of task {TaskId} for tenant {TenantId} failed.", taskId, tenantId);
            }
        }

        return claimed.Count;
    }
}
