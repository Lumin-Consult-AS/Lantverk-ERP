using System.Text.Json;
using Erp.Core.Domain.Apps;
using Erp.Core.Domain.Tenancy;
using Erp.Core.Infrastructure.Apps;
using Erp.Core.Infrastructure.Events;
using Erp.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NpgsqlTypes;

namespace Erp.Core.Infrastructure.Scheduling;

public sealed class ScheduleOptions
{
    public const string SectionName = "Schedules";

    /// <summary>How often the worker looks for due schedules.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// When a run is claimed, the schedule is pushed this far ahead; if the run fails the schedule simply comes due again
    /// after this time (retry), and if it succeeds it is moved to its next slot.
    /// </summary>
    public TimeSpan RetryAfter { get; set; } = TimeSpan.FromMinutes(15);
}

public sealed record ScheduleInfo(
    string AppId,
    string ScheduleId,
    string Label,
    string CommandId,
    string? At,
    int? EveryMinutes,
    DateTimeOffset NextRunAt,
    DateTimeOffset? LastRunAt,
    string? LastStatus,
    string? LastMessage);

public sealed record ScheduleRunResult(string AppId, string ScheduleId, string Status, string? Message);

/// <summary>
/// Runs the schedules of the current tenant's installed apps (ADR-0025). A run is a call to one of the app's commands with
/// no arguments. A schedule is claimed before it runs (so two workers do not both run it), and on failure it comes due
/// again after <see cref="ScheduleOptions.RetryAfter"/>. Commands must therefore be safe to run again.
/// </summary>
public sealed class ScheduleService(
    CoreDbContext dbContext,
    ITenantContext tenantContext,
    AppHostService appHost,
    ScheduleOptions options,
    ILogger<ScheduleService> logger)
{
    /// <summary>Makes the stored state match the installed apps: new schedules are due at once; removed ones are forgotten.</summary>
    public async Task SyncAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var defined = await LoadDefinitionsAsync(cancellationToken);
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);

        foreach (var definition in defined)
        {
            await using var cmd = TenantDb.Create(
                connection,
                null,
                """
                INSERT INTO core.app_schedule (tenant_id, app_id, schedule_id, next_run_at)
                VALUES (current_setting('app.tenant_id')::uuid, @app, @schedule, @now)
                ON CONFLICT DO NOTHING;
                """);
            TenantDb.Add(cmd, "app", NpgsqlDbType.Varchar, definition.OwnerApp);
            TenantDb.Add(cmd, "schedule", NpgsqlDbType.Varchar, definition.Id);
            TenantDb.Add(cmd, "now", NpgsqlDbType.TimestampTz, now);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var cleanup = TenantDb.Create(
            connection,
            null,
            "DELETE FROM core.app_schedule WHERE NOT ((app_id || '|' || schedule_id) = ANY (@keep));");
        TenantDb.Add(cleanup, "keep", NpgsqlDbType.Array | NpgsqlDbType.Text, defined.Select(d => $"{d.OwnerApp}|{d.Id}").ToArray());
        await cleanup.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Runs every schedule that is due at <paramref name="now"/>.</summary>
    public async Task<IReadOnlyList<ScheduleRunResult>> RunDueAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await SyncAsync(now, cancellationToken);
        var definitions = await LoadDefinitionsAsync(cancellationToken);
        var results = new List<ScheduleRunResult>();

        foreach (var definition in definitions)
        {
            if (await ClaimAsync(definition, now, onlyIfDue: true, cancellationToken))
            {
                results.Add(await RunClaimedAsync(definition, now, cancellationToken));
            }
        }

        return results;
    }

    /// <summary>Runs one schedule now, whatever its timetable says (for an administrator).</summary>
    public async Task<ScheduleRunResult> RunNowAsync(string appId, string scheduleId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await SyncAsync(now, cancellationToken);
        var definition = (await LoadDefinitionsAsync(cancellationToken)).FirstOrDefault(
                d => string.Equals(d.OwnerApp, appId, StringComparison.OrdinalIgnoreCase)
                     && string.Equals(d.Id, scheduleId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Schedule '{scheduleId}' of app '{appId}' was not found.");

        await ClaimAsync(definition, now, onlyIfDue: false, cancellationToken);
        return await RunClaimedAsync(definition, now, cancellationToken);
    }

    public async Task<IReadOnlyList<ScheduleInfo>> ListAsync(CancellationToken cancellationToken)
    {
        var definitions = await LoadDefinitionsAsync(cancellationToken);
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        var state = new Dictionary<string, (DateTimeOffset Next, DateTimeOffset? Last, string? Status, string? Message)>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = TenantDb.Create(
                         connection,
                         null,
                         "SELECT app_id, schedule_id, next_run_at, last_run_at, last_status, last_message FROM core.app_schedule;"))
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                state[$"{reader.GetString(0)}|{reader.GetString(1)}"] = (
                    reader.GetFieldValue<DateTimeOffset>(2),
                    reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5));
            }
        }

        return definitions
            .Where(d => state.ContainsKey($"{d.OwnerApp}|{d.Id}"))
            .Select(d =>
            {
                var s = state[$"{d.OwnerApp}|{d.Id}"];
                return new ScheduleInfo(
                    d.OwnerApp, d.Id, d.Label, d.CommandId, d.At?.ToString("HH:mm"), d.EveryMinutes, s.Next, s.Last, s.Status, s.Message);
            })
            .OrderBy(i => i.AppId)
            .ThenBy(i => i.ScheduleId)
            .ToList();
    }

    // ------------------------------------------------------------------ internals

    private async Task<List<ScheduleDefinition>> LoadDefinitionsAsync(CancellationToken cancellationToken) =>
        (await dbContext.InstalledApps.AsNoTracking().ToListAsync(cancellationToken))
            .SelectMany(a => JsonSerializer.Deserialize<AppManifest>(a.ManifestJson, AppHostService.JsonOptions)?.Schedules ?? [])
            .ToList();

    /// <summary>
    /// Takes the schedule for this run by pushing its due time ahead by the retry delay. Returns false if it is not due, or
    /// another worker took it first.
    /// </summary>
    private async Task<bool> ClaimAsync(ScheduleDefinition definition, DateTimeOffset now, bool onlyIfDue, CancellationToken cancellationToken)
    {
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        await using var cmd = TenantDb.Create(
            connection,
            null,
            $"""
            UPDATE core.app_schedule SET next_run_at = @lease
             WHERE app_id = @app AND schedule_id = @schedule {(onlyIfDue ? "AND next_run_at <= @now" : string.Empty)};
            """);
        TenantDb.Add(cmd, "app", NpgsqlDbType.Varchar, definition.OwnerApp);
        TenantDb.Add(cmd, "schedule", NpgsqlDbType.Varchar, definition.Id);
        TenantDb.Add(cmd, "now", NpgsqlDbType.TimestampTz, now);
        TenantDb.Add(cmd, "lease", NpgsqlDbType.TimestampTz, now.Add(options.RetryAfter));
        return await cmd.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private async Task<ScheduleRunResult> RunClaimedAsync(ScheduleDefinition definition, DateTimeOffset now, CancellationToken cancellationToken)
    {
        string status;
        string? message;
        try
        {
            var response = await appHost.RunCommandAsync(
                definition.OwnerApp, definition.CommandId, Guid.NewGuid(), Guid.Empty, "{}", [], conflict: false, forced: false, cancellationToken);
            status = response.Outcome is "applied" or "adjusted" ? "ok" : "failed";
            message = response.Message;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Schedule {Schedule} of app {App} failed.", definition.Id, definition.OwnerApp);
            status = "failed";
            message = ex is Grpc.Core.RpcException rpc && !string.IsNullOrWhiteSpace(rpc.Status.Detail) ? rpc.Status.Detail : ex.Message;
        }

        // On success the schedule moves to its next slot; on failure it keeps the retry time set when it was claimed.
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        await using var cmd = TenantDb.Create(
            connection,
            null,
            $"""
            UPDATE core.app_schedule
               SET last_run_at = @now, last_status = @status, last_message = @message
                   {(status == "ok" ? ", next_run_at = @next" : string.Empty)}
             WHERE app_id = @app AND schedule_id = @schedule;
            """);
        TenantDb.Add(cmd, "app", NpgsqlDbType.Varchar, definition.OwnerApp);
        TenantDb.Add(cmd, "schedule", NpgsqlDbType.Varchar, definition.Id);
        TenantDb.Add(cmd, "now", NpgsqlDbType.TimestampTz, now);
        TenantDb.Add(cmd, "status", NpgsqlDbType.Varchar, status);
        TenantDb.Add(cmd, "message", NpgsqlDbType.Text, message);
        if (status == "ok")
        {
            TenantDb.Add(cmd, "next", NpgsqlDbType.TimestampTz, definition.NextAfter(now));
        }

        await cmd.ExecuteNonQueryAsync(cancellationToken);
        return new ScheduleRunResult(definition.OwnerApp, definition.Id, status, message);
    }
}
