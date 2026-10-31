using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Erp.Core.Domain.Apps;
using Erp.Core.Domain.Data;
using Erp.Core.Domain.Metadata;
using Erp.Core.Domain.Tenancy;
using Erp.Core.Infrastructure.Apps;
using Erp.Core.Infrastructure.Data;
using Erp.Core.Infrastructure.Events;
using Erp.Core.Infrastructure.Persistence;
using Erp.Sdk.Contracts.V1;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Erp.Core.Infrastructure.Sync;

/// <summary>
/// The core's API for offline clients (ADR-0014): registered devices, delta sync of app-defined record sets on a cursor
/// taken from the change log, reservations, and a queue of commands that apps carry out when the client reconnects.
/// The core knows nothing about what the records or commands mean; apps define sets, commands and conflict rules in
/// their manifest and carry commands out through the ordinary data API, so validation, hooks and permissions are the
/// same as for an online user.
/// </summary>
public sealed class SyncService(
    CoreDbContext dbContext,
    ITenantContext tenantContext,
    RecordDataService recordData,
    AppHostService appHost,
    IInternalTokenIssuer tokenIssuer,
    SyncOptions options)
{
    private const string DeviceColumns = "id, user_id, name, status, last_seen_at, blocked_at, blocked_reason, created_at";

    private const string CommandColumns =
        "id, device_id, client_command_id, command_id, status, message, result::text, conflicts::text, args::text, targets::text";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    // ------------------------------------------------------------------ devices

    public async Task<SyncDeviceInfo> RegisterDeviceAsync(string name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new RecordValidationException("Device name is required.", "name");
        }

        var tenantId = RequireTenant();
        var userId = RequireUser();
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        await using var cmd = TenantDb.Create(
            connection,
            null,
            $"""
            INSERT INTO core.sync_device (id, tenant_id, created_by, updated_by, user_id, name, last_seen_at)
            VALUES (@id, @tenant, @user, @user, @user, @name, now())
            RETURNING {DeviceColumns};
            """);
        TenantDb.Add(cmd, "id", NpgsqlDbType.Uuid, Guid.NewGuid());
        TenantDb.Add(cmd, "tenant", NpgsqlDbType.Uuid, tenantId);
        TenantDb.Add(cmd, "user", NpgsqlDbType.Uuid, userId);
        TenantDb.Add(cmd, "name", NpgsqlDbType.Varchar, name.Trim().Length > 200 ? name.Trim()[..200] : name.Trim());
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return ReadDevice(reader);
    }

    /// <summary>The caller's own devices; an administrator sees every device of the tenant.</summary>
    public async Task<IReadOnlyList<SyncDeviceInfo>> ListDevicesAsync(bool all, CancellationToken cancellationToken)
    {
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        await using var cmd = TenantDb.Create(
            connection,
            null,
            $"SELECT {DeviceColumns} FROM core.sync_device WHERE (@all OR user_id = @user) ORDER BY created_at;");
        TenantDb.Add(cmd, "all", NpgsqlDbType.Boolean, all);
        TenantDb.Add(cmd, "user", NpgsqlDbType.Uuid, RequireUser());
        var result = new List<SyncDeviceInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadDevice(reader));
        }

        return result;
    }

    /// <summary>Blocks or unblocks a device centrally. A blocked device can no longer pull, push, reserve or renew its token.</summary>
    public async Task<SyncDeviceInfo> SetBlockedAsync(Guid deviceId, bool blocked, string? reason, CancellationToken cancellationToken)
    {
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        await using var cmd = TenantDb.Create(
            connection,
            null,
            $"""
            UPDATE core.sync_device
               SET status = @status, blocked_at = CASE WHEN @blocked THEN now() ELSE NULL END,
                   blocked_by = CASE WHEN @blocked THEN @user ELSE NULL END,
                   blocked_reason = CASE WHEN @blocked THEN @reason ELSE NULL END,
                   updated_at = now(), updated_by = @user, row_version = row_version + 1
             WHERE id = @id
            RETURNING {DeviceColumns};
            """);
        TenantDb.Add(cmd, "id", NpgsqlDbType.Uuid, deviceId);
        TenantDb.Add(cmd, "status", NpgsqlDbType.Varchar, blocked ? "blocked" : "active");
        TenantDb.Add(cmd, "blocked", NpgsqlDbType.Boolean, blocked);
        TenantDb.Add(cmd, "user", NpgsqlDbType.Uuid, RequireUser());
        TenantDb.Add(cmd, "reason", NpgsqlDbType.Text, string.IsNullOrWhiteSpace(reason) ? null : reason.Trim());
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadDevice(reader)
            : throw new KeyNotFoundException($"Device '{deviceId}' was not found.");
    }

    /// <summary>A fresh short-lived token for the device's user. Called when the device reconnects; refused when it is blocked.</summary>
    public async Task<SyncDeviceToken> IssueTokenAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var device = await RequireActiveDeviceAsync(deviceId, cancellationToken);
        var token = tokenIssuer.IssueShortLivedCallToken(
            RequireTenant(), device.UserId, tenantContext.CompanyId, tenantContext.Roles, options.TokenLifetime);
        return new SyncDeviceToken(token, DateTimeOffset.UtcNow.Add(options.TokenLifetime));
    }

    // ------------------------------------------------------------------ sync sets

    public async Task<IReadOnlyList<SyncSetInfo>> ListSetsAsync(CancellationToken cancellationToken) =>
        (await LoadSetsAsync(cancellationToken))
            .Select(s => new SyncSetInfo(s.Id, s.OwnerApp, s.Label, s.Objects.Select(o => o.Object).ToList()))
            .ToList();

    /// <summary>
    /// Downloads a sync set. Without a cursor the whole set is returned (a snapshot, in pages); with the cursor of an earlier
    /// pull only what changed since is returned: changed records and the ids of records that were deleted or left the set.
    /// </summary>
    public async Task<SyncPullResult> PullAsync(
        Guid deviceId,
        string setId,
        string? cursor,
        string? continuation,
        int? limit,
        CancellationToken cancellationToken)
    {
        await RequireActiveDeviceAsync(deviceId, cancellationToken);
        var set = (await LoadSetsAsync(cancellationToken)).FirstOrDefault(s => string.Equals(s.Id, setId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Sync set '{setId}' was not found.");
        var pageSize = Math.Clamp(limit ?? options.DefaultPullLimit, 1, options.MaxPullLimit);

        return string.IsNullOrWhiteSpace(cursor)
            ? await PullSnapshotAsync(set, continuation, pageSize, cancellationToken)
            : await PullDeltaAsync(set, cursor, pageSize, cancellationToken);
    }

    private async Task<SyncPullResult> PullSnapshotAsync(
        SyncSetDefinition set,
        string? continuationToken,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var state = continuationToken is null
            ? new SnapshotState(await GetHeadAsync(cancellationToken), 0, 0)
            : DecodeContinuation(continuationToken);

        var records = new List<SyncRecord>();
        var objectIndex = state.ObjectIndex;
        var skip = state.Skip;
        while (objectIndex < set.Objects.Count && records.Count < pageSize)
        {
            var entry = set.Objects[objectIndex];
            var take = Math.Min(1000, pageSize - records.Count);
            var page = await recordData.QueryAsync(
                entry.Object,
                new DataQuery { Filter = ParseFilter(entry.Filter), Top = take, Skip = skip },
                tenantContext.CompanyId,
                cancellationToken);
            records.AddRange(page.Select(r => new SyncRecord(entry.Object, r)));

            if (page.Count < take)
            {
                objectIndex++;
                skip = 0;
            }
            else
            {
                skip += page.Count;
            }
        }

        var finished = objectIndex >= set.Objects.Count;
        return new SyncPullResult(
            finished ? state.Head : null,
            !finished,
            finished ? null : EncodeContinuation(new SnapshotState(state.Head, objectIndex, skip)),
            records,
            []);
    }

    private async Task<SyncPullResult> PullDeltaAsync(SyncSetDefinition set, string cursor, int pageSize, CancellationToken cancellationToken)
    {
        var (xid, seq) = ParseCursor(cursor);
        var tables = set.Objects
            .Select(o => (o.Object, Table: IdentifierNaming.SplitObjectId(o.Object)))
            .ToList();

        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        var where = new StringBuilder();
        await using var cmd = connection.CreateCommand();
        for (var i = 0; i < tables.Count; i++)
        {
            where.Append(i == 0 ? string.Empty : " OR ").Append($"(schema_name = @s{i} AND table_name = @t{i})");
            TenantDb.Add(cmd, $"s{i}", NpgsqlDbType.Varchar, tables[i].Table.Schema);
            TenantDb.Add(cmd, $"t{i}", NpgsqlDbType.Varchar, tables[i].Table.Table);
        }

        // Only changes of transactions that have finished: xid below the oldest transaction still running.
        cmd.CommandText =
            $"""
            SELECT xid::text AS xid_text, seq, schema_name, table_name, record_id, operation
              FROM core.change_log
             WHERE (xid, seq) > (@xid::xid8, @seq)
               AND xid < pg_snapshot_xmin(pg_current_snapshot())
               AND ({where})
             ORDER BY xid, seq
             LIMIT @limit;
            """;
        TenantDb.Add(cmd, "xid", NpgsqlDbType.Text, xid);
        TenantDb.Add(cmd, "seq", NpgsqlDbType.Bigint, seq);
        TenantDb.Add(cmd, "limit", NpgsqlDbType.Integer, pageSize);

        var changes = new List<(string Object, Guid Record, char Operation)>();
        var next = (Xid: xid, Seq: seq);
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                next = (reader.GetString(0), reader.GetInt64(1));
                var objectId = tables.First(t => t.Table.Schema == reader.GetString(2) && t.Table.Table == reader.GetString(3)).Object;
                changes.Add((objectId, reader.GetGuid(4), reader.GetString(5)[0]));
            }
        }

        // The latest state of every record that changed, in the order it first changed
        var records = new List<SyncRecord>();
        var removed = new List<SyncRemoved>();
        foreach (var group in changes.GroupBy(c => (c.Object, c.Record)))
        {
            var (objectId, id) = group.Key;
            if (group.Last().Operation == 'D')
            {
                removed.Add(new SyncRemoved(objectId, id, "deleted"));
                continue;
            }

            var current = await recordData.GetAsync(objectId, id, tenantContext.CompanyId, cancellationToken);
            if (current is null)
            {
                removed.Add(new SyncRemoved(objectId, id, "deleted"));
                continue;
            }

            var filter = ParseFilter(set.Objects.First(o => o.Object == objectId).Filter);
            if (filter is not null && !WhenRuleEvaluator.Evaluate(filter, current.Fields))
            {
                removed.Add(new SyncRemoved(objectId, id, "left-set"));
                continue;
            }

            records.Add(new SyncRecord(objectId, current));
        }

        return new SyncPullResult($"{next.Xid}.{next.Seq}", changes.Count == pageSize, null, records, removed);
    }

    /// <summary>The cursor that marks "everything so far": the newest change of a finished transaction, or <c>0.0</c>.</summary>
    private async Task<string> GetHeadAsync(CancellationToken cancellationToken)
    {
        // The output column is aliased: ORDER BY xid would otherwise sort by the text cast ("994" > "1471").
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        await using var cmd = TenantDb.Create(
            connection,
            null,
            """
            SELECT xid::text AS xid_text, seq FROM core.change_log
             WHERE xid < pg_snapshot_xmin(pg_current_snapshot())
             ORDER BY xid DESC, seq DESC LIMIT 1;
            """);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? $"{reader.GetString(0)}.{reader.GetInt64(1)}" : "0.0";
    }

    // ------------------------------------------------------------------ reservations

    /// <summary>
    /// Reserves records for a device so that two terminals do not work on the same one. A reservation lasts until it
    /// expires or is released; records reserved by another device are reported back, not taken.
    /// </summary>
    public async Task<ReserveResult> ReserveAsync(
        Guid deviceId,
        string objectId,
        IReadOnlyCollection<Guid> recordIds,
        int ttlMinutes,
        CancellationToken cancellationToken)
    {
        var device = await RequireActiveDeviceAsync(deviceId, cancellationToken);
        var tenantId = RequireTenant();
        var expires = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(ttlMinutes <= 0 ? 60 : ttlMinutes, 1, options.MaxReservationMinutes));
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);

        var reserved = new List<ReservationInfo>();
        var conflicts = new List<ReservationConflict>();
        foreach (var recordId in recordIds.Distinct())
        {
            // Reading the record checks that it exists and that the user may see it.
            if (await recordData.GetAsync(objectId, recordId, tenantContext.CompanyId, cancellationToken) is null)
            {
                conflicts.Add(new ReservationConflict(recordId, null, null, "not-found"));
                continue;
            }

            await using var cmd = TenantDb.Create(
                connection,
                null,
                """
                INSERT INTO core.sync_reservation (id, tenant_id, created_by, updated_by, device_id, user_id, object_id, record_id, expires_at)
                VALUES (@id, @tenant, @user, @user, @device, @user, @object, @record, @expires)
                ON CONFLICT (tenant_id, object_id, record_id) DO UPDATE
                   SET device_id = EXCLUDED.device_id, user_id = EXCLUDED.user_id, expires_at = EXCLUDED.expires_at,
                       updated_at = now(), row_version = core.sync_reservation.row_version + 1
                 WHERE core.sync_reservation.expires_at <= now() OR core.sync_reservation.device_id = EXCLUDED.device_id
                RETURNING id;
                """);
            TenantDb.Add(cmd, "id", NpgsqlDbType.Uuid, Guid.NewGuid());
            TenantDb.Add(cmd, "tenant", NpgsqlDbType.Uuid, tenantId);
            TenantDb.Add(cmd, "user", NpgsqlDbType.Uuid, device.UserId);
            TenantDb.Add(cmd, "device", NpgsqlDbType.Uuid, deviceId);
            TenantDb.Add(cmd, "object", NpgsqlDbType.Varchar, objectId);
            TenantDb.Add(cmd, "record", NpgsqlDbType.Uuid, recordId);
            TenantDb.Add(cmd, "expires", NpgsqlDbType.TimestampTz, expires);

            if (await cmd.ExecuteScalarAsync(cancellationToken) is Guid id)
            {
                reserved.Add(new ReservationInfo(id, deviceId, objectId, recordId, expires));
            }
            else
            {
                var holder = await FindReservationAsync(connection, objectId, recordId, cancellationToken);
                conflicts.Add(new ReservationConflict(recordId, holder?.DeviceId, holder?.ExpiresAt, "reserved"));
            }
        }

        return new ReserveResult(reserved, conflicts);
    }

    /// <summary>Releases the device's reservations (all of them, or just the given records).</summary>
    public async Task<int> ReleaseAsync(Guid deviceId, IReadOnlyCollection<Guid>? recordIds, CancellationToken cancellationToken)
    {
        await RequireActiveDeviceAsync(deviceId, cancellationToken);
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        await using var cmd = TenantDb.Create(
            connection,
            null,
            "DELETE FROM core.sync_reservation WHERE device_id = @device AND (@all OR record_id = ANY (@records));");
        TenantDb.Add(cmd, "device", NpgsqlDbType.Uuid, deviceId);
        TenantDb.Add(cmd, "all", NpgsqlDbType.Boolean, recordIds is null || recordIds.Count == 0);
        TenantDb.Add(cmd, "records", NpgsqlDbType.Array | NpgsqlDbType.Uuid, (recordIds ?? []).ToArray());
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ReservationInfo>> ListReservationsAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        await RequireActiveDeviceAsync(deviceId, cancellationToken);
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        await using var cmd = TenantDb.Create(
            connection,
            null,
            "SELECT id, device_id, object_id, record_id, expires_at FROM core.sync_reservation WHERE device_id = @device AND expires_at > now() ORDER BY expires_at;");
        TenantDb.Add(cmd, "device", NpgsqlDbType.Uuid, deviceId);
        var result = new List<ReservationInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadReservation(reader));
        }

        return result;
    }

    // ------------------------------------------------------------------ commands

    /// <summary>
    /// Processes the device's queued commands in order. Each command has a client-generated id: sending it again (a retry after
    /// a lost answer) returns the stored result and does not run it twice.
    /// </summary>
    public async Task<IReadOnlyList<CommandOutcome>> PushAsync(
        Guid deviceId,
        IReadOnlyList<CommandSubmission> commands,
        CancellationToken cancellationToken)
    {
        await RequireActiveDeviceAsync(deviceId, cancellationToken);
        var definitions = await LoadCommandsAsync(cancellationToken);

        // A command can change a record that a later command in the same upload also touches. The device cannot know the
        // version its own earlier command produced, so the core remembers it: (record, version the device saw) -> version now.
        var advanced = new Dictionary<(string ObjectId, Guid RecordId), (long Base, long Version)>();

        var outcomes = new List<CommandOutcome>();
        foreach (var command in commands)
        {
            outcomes.Add(await ProcessAsync(deviceId, command, definitions, advanced, cancellationToken));
        }

        return outcomes;
    }

    public async Task<IReadOnlyList<CommandOutcome>> ListCommandsAsync(string? status, Guid? deviceId, int top, CancellationToken cancellationToken)
    {
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        await using var cmd = TenantDb.Create(
            connection,
            null,
            $"""
            SELECT {CommandColumns} FROM core.sync_command
             WHERE (@status::text IS NULL OR status = @status) AND (@device::uuid IS NULL OR device_id = @device)
             ORDER BY received_at DESC LIMIT @top;
            """);
        TenantDb.Add(cmd, "status", NpgsqlDbType.Text, string.IsNullOrWhiteSpace(status) ? null : status);
        TenantDb.Add(cmd, "device", NpgsqlDbType.Uuid, deviceId);
        TenantDb.Add(cmd, "top", NpgsqlDbType.Integer, Math.Clamp(top, 1, 200));
        var result = new List<CommandOutcome>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadOutcome(reader, duplicate: false));
        }

        return result;
    }

    /// <summary>
    /// Settles a command waiting in the manual queue: <c>apply</c> carries it out now, despite the conflict, as the person
    /// resolving it; <c>discard</c> drops it.
    /// </summary>
    public async Task<CommandOutcome> ResolveAsync(Guid rowId, string action, CancellationToken cancellationToken)
    {
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        var existing = await LoadCommandRowAsync(connection, rowId, cancellationToken)
            ?? throw new KeyNotFoundException($"Command '{rowId}' was not found.");
        if (existing.Status != "manual")
        {
            throw new SyncCommandStateException($"Command is {existing.Status}; only commands in the manual queue can be resolved.");
        }

        if (string.Equals(action, "discard", StringComparison.OrdinalIgnoreCase))
        {
            return await FinishAsync(connection, rowId, "discarded", "Discarded by a person.", null, null, cancellationToken);
        }

        if (!string.Equals(action, "apply", StringComparison.OrdinalIgnoreCase))
        {
            throw new RecordValidationException("Action must be 'apply' or 'discard'.", "action");
        }

        var definition = (await LoadCommandsAsync(cancellationToken)).FirstOrDefault(c => string.Equals(c.Id, existing.CommandId, StringComparison.OrdinalIgnoreCase));
        if (definition is null)
        {
            return await FinishAsync(connection, rowId, "rejected", "The command is no longer defined by any installed app.", null, null, cancellationToken);
        }

        var targets = existing.Targets ?? [];
        var infos = await BuildTargetsAsync(targets, cancellationToken);
        return await RunHandlerAsync(connection, rowId, definition, existing, infos, conflict: false, forced: true, cancellationToken);
    }

    private async Task<CommandOutcome> ProcessAsync(
        Guid deviceId,
        CommandSubmission command,
        IReadOnlyList<CommandDefinition> definitions,
        Dictionary<(string ObjectId, Guid RecordId), (long Base, long Version)> advanced,
        CancellationToken cancellationToken)
    {
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        var targets = command.Targets ?? [];

        // Idempotency: the first arrival claims the id; a retry gets the stored answer and nothing runs twice.
        var rowId = await ClaimAsync(connection, deviceId, command, targets, cancellationToken);
        if (rowId is null)
        {
            var stored = await LoadByClientIdAsync(connection, deviceId, command.Id, cancellationToken)
                ?? throw new InvalidOperationException("Command row disappeared.");
            return ReadStored(stored, duplicate: true);
        }

        try
        {
            var definition = definitions.FirstOrDefault(d => string.Equals(d.Id, command.Name, StringComparison.OrdinalIgnoreCase));
            if (definition is null)
            {
                return await FinishAsync(connection, rowId.Value, "rejected", $"No installed app defines command '{command.Name}'.", null, null, cancellationToken);
            }

            var infos = await BuildTargetsAsync(targets, cancellationToken);
            var conflicts = await FindConflictsAsync(deviceId, infos, advanced, cancellationToken);

            if (conflicts.Count > 0)
            {
                var reservedByOther = conflicts.Any(c => c.Reason == "reserved");
                var rule = reservedByOther ? CommandConflictRules.Reject : definition.OnConflict.ToLowerInvariant();

                if (rule == CommandConflictRules.Reject)
                {
                    return await FinishAsync(
                        connection, rowId.Value, "conflict",
                        reservedByOther ? "A record is reserved by another device." : "A record changed since the device downloaded it.",
                        null, conflicts, cancellationToken);
                }

                if (rule == CommandConflictRules.Manual)
                {
                    return await FinishAsync(
                        connection, rowId.Value, "manual", "A record changed; the command waits for a person to apply or discard it.",
                        null, conflicts, cancellationToken);
                }
            }

            var row = (await LoadCommandRowAsync(connection, rowId.Value, cancellationToken))!;
            var outcome = await RunHandlerAsync(connection, rowId.Value, definition, row, infos, conflict: conflicts.Count > 0, forced: false, cancellationToken, conflicts);

            // Only a command that ran on what the device saw produced a version the device "knows"; one that ran over a
            // conflict has not brought the device up to date, so later commands must still see the record as stale.
            if (outcome.Status is "applied" or "adjusted" && conflicts.Count == 0)
            {
                foreach (var info in infos)
                {
                    var now = await recordData.GetAsync(info.Input.ObjectId, info.Input.RecordId, tenantContext.CompanyId, cancellationToken);
                    if (now is not null)
                    {
                        advanced[(info.Input.ObjectId, info.Input.RecordId)] = (info.Input.BaseRowVersion, now.RowVersion);
                    }
                }
            }

            return outcome;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await FinishAsync(connection, rowId.Value, "failed", ex is Grpc.Core.RpcException rpc ? rpc.Status.Detail : ex.Message, null, null, cancellationToken);
        }
    }

    private async Task<CommandOutcome> RunHandlerAsync(
        DbConnection connection,
        Guid rowId,
        CommandDefinition definition,
        CommandRow row,
        IReadOnlyList<TargetInfo> infos,
        bool conflict,
        bool forced,
        CancellationToken cancellationToken,
        IReadOnlyList<CommandConflict>? conflicts = null)
    {
        try
        {
            var response = await appHost.RunCommandAsync(
                definition.OwnerApp,
                definition.Id,
                row.ClientCommandId,
                row.DeviceId,
                row.ArgsJson,
                infos.Select(i => new CommandTargetProto
                {
                    ObjectId = i.Input.ObjectId,
                    RecordId = i.Input.RecordId.ToString("D"),
                    BaseRowVersion = i.Input.BaseRowVersion,
                    CurrentRowVersion = i.Current?.RowVersion ?? 0,
                    Exists = i.Current is not null
                }),
                conflict,
                forced,
                cancellationToken);

            var status = response.Outcome is "applied" or "adjusted" or "rejected" ? response.Outcome : "failed";
            return await FinishAsync(
                connection, rowId, status, string.IsNullOrEmpty(response.Message) ? null : response.Message,
                string.IsNullOrWhiteSpace(response.ResultJson) ? null : response.ResultJson,
                status == "adjusted" ? conflicts : null,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await FinishAsync(connection, rowId, "failed", ex is Grpc.Core.RpcException rpc ? rpc.Status.Detail : ex.Message, null, null, cancellationToken);
        }
    }

    private sealed record TargetInfo(CommandTargetInput Input, RecordData? Current);

    private async Task<IReadOnlyList<TargetInfo>> BuildTargetsAsync(IReadOnlyList<CommandTargetInput> targets, CancellationToken cancellationToken)
    {
        var result = new List<TargetInfo>();
        foreach (var target in targets)
        {
            RecordData? current = null;
            try
            {
                current = await recordData.GetAsync(target.ObjectId, target.RecordId, tenantContext.CompanyId, cancellationToken);
            }
            catch (ObjectNotFoundException)
            {
                // Unknown object: treated like a missing record.
            }

            result.Add(new TargetInfo(target, current));
        }

        return result;
    }

    private async Task<IReadOnlyList<CommandConflict>> FindConflictsAsync(
        Guid deviceId,
        IReadOnlyList<TargetInfo> infos,
        Dictionary<(string ObjectId, Guid RecordId), (long Base, long Version)> advanced,
        CancellationToken cancellationToken)
    {
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        var conflicts = new List<CommandConflict>();
        foreach (var info in infos)
        {
            var target = info.Input;

            // The version the device is expected to have: what it saw, or what its own earlier command left behind.
            var expected = advanced.TryGetValue((target.ObjectId, target.RecordId), out var own) && own.Base == target.BaseRowVersion
                ? own.Version
                : target.BaseRowVersion;
            var holder = await FindReservationAsync(connection, target.ObjectId, target.RecordId, cancellationToken);
            if (holder is not null && holder.DeviceId != deviceId)
            {
                conflicts.Add(new CommandConflict(target.ObjectId, target.RecordId, "reserved", target.BaseRowVersion, info.Current?.RowVersion, info.Current));
            }
            else if (info.Current is null)
            {
                conflicts.Add(new CommandConflict(target.ObjectId, target.RecordId, "deleted", target.BaseRowVersion, null, null));
            }
            else if (expected > 0 && info.Current.RowVersion != expected)
            {
                conflicts.Add(new CommandConflict(target.ObjectId, target.RecordId, "stale", target.BaseRowVersion, info.Current.RowVersion, info.Current));
            }
        }

        return conflicts;
    }

    // ------------------------------------------------------------------ persistence of commands

    private sealed record CommandRow(
        Guid Id,
        Guid DeviceId,
        Guid ClientCommandId,
        string CommandId,
        string Status,
        string? Message,
        string? ResultJson,
        string? ConflictsJson,
        string ArgsJson,
        IReadOnlyList<CommandTargetInput>? Targets);

    private async Task<Guid?> ClaimAsync(
        DbConnection connection,
        Guid deviceId,
        CommandSubmission command,
        IReadOnlyList<CommandTargetInput> targets,
        CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        await using var cmd = TenantDb.Create(
            connection,
            null,
            """
            INSERT INTO core.sync_command
                (id, tenant_id, created_by, updated_by, device_id, client_command_id, command_id, args, targets, status)
            VALUES (@id, @tenant, @user, @user, @device, @client, @command, @args::jsonb, @targets::jsonb, 'processing')
            ON CONFLICT (tenant_id, device_id, client_command_id) DO NOTHING
            RETURNING id;
            """);
        TenantDb.Add(cmd, "id", NpgsqlDbType.Uuid, Guid.NewGuid());
        TenantDb.Add(cmd, "tenant", NpgsqlDbType.Uuid, tenantId);
        TenantDb.Add(cmd, "user", NpgsqlDbType.Uuid, RequireUser());
        TenantDb.Add(cmd, "device", NpgsqlDbType.Uuid, deviceId);
        TenantDb.Add(cmd, "client", NpgsqlDbType.Uuid, command.Id);
        TenantDb.Add(cmd, "command", NpgsqlDbType.Varchar, command.Name.Length > 200 ? command.Name[..200] : command.Name);
        TenantDb.Add(cmd, "args", NpgsqlDbType.Text, command.Args.ValueKind == JsonValueKind.Undefined ? "{}" : command.Args.GetRawText());
        TenantDb.Add(cmd, "targets", NpgsqlDbType.Text, JsonSerializer.Serialize(targets, JsonOptions));
        return await cmd.ExecuteScalarAsync(cancellationToken) as Guid?;
    }

    private async Task<CommandOutcome> FinishAsync(
        DbConnection connection,
        Guid rowId,
        string status,
        string? message,
        string? resultJson,
        IReadOnlyList<CommandConflict>? conflicts,
        CancellationToken cancellationToken)
    {
        await using var cmd = TenantDb.Create(
            connection,
            null,
            $"""
            UPDATE core.sync_command
               SET status = @status, message = @message, result = @result::jsonb, conflicts = @conflicts::jsonb, processed_at = now(),
                   updated_at = now(), updated_by = @user, row_version = row_version + 1
             WHERE id = @id
            RETURNING {CommandColumns};
            """);
        TenantDb.Add(cmd, "id", NpgsqlDbType.Uuid, rowId);
        TenantDb.Add(cmd, "status", NpgsqlDbType.Varchar, status);
        TenantDb.Add(cmd, "message", NpgsqlDbType.Text, message);
        TenantDb.Add(cmd, "result", NpgsqlDbType.Text, resultJson);
        TenantDb.Add(cmd, "conflicts", NpgsqlDbType.Text, conflicts is null ? null : JsonSerializer.Serialize(conflicts, JsonOptions));
        TenantDb.Add(cmd, "user", NpgsqlDbType.Uuid, RequireUser());
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return ReadOutcome(reader, duplicate: false);
    }

    private static async Task<CommandRow?> LoadCommandRowAsync(DbConnection connection, Guid rowId, CancellationToken cancellationToken)
    {
        await using var cmd = TenantDb.Create(connection, null, $"SELECT {CommandColumns} FROM core.sync_command WHERE id = @id;");
        TenantDb.Add(cmd, "id", NpgsqlDbType.Uuid, rowId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRow(reader) : null;
    }

    private static async Task<CommandRow?> LoadByClientIdAsync(DbConnection connection, Guid deviceId, Guid clientId, CancellationToken cancellationToken)
    {
        await using var cmd = TenantDb.Create(
            connection, null, $"SELECT {CommandColumns} FROM core.sync_command WHERE device_id = @device AND client_command_id = @client;");
        TenantDb.Add(cmd, "device", NpgsqlDbType.Uuid, deviceId);
        TenantDb.Add(cmd, "client", NpgsqlDbType.Uuid, clientId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRow(reader) : null;
    }

    private static CommandRow ReadRow(DbDataReader r) => new(
        r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5),
        r.IsDBNull(6) ? null : r.GetString(6),
        r.IsDBNull(7) ? null : r.GetString(7),
        r.GetString(8),
        JsonSerializer.Deserialize<List<CommandTargetInput>>(r.GetString(9), JsonOptions));

    private static CommandOutcome ReadOutcome(DbDataReader r, bool duplicate) => ReadStored(ReadRow(r), duplicate);

    private static CommandOutcome ReadStored(CommandRow row, bool duplicate) => new(
        row.Id,
        row.ClientCommandId,
        row.CommandId,
        row.Status,
        row.Message,
        row.ResultJson is null ? null : JsonDocument.Parse(row.ResultJson).RootElement.Clone(),
        row.ConflictsJson is null ? null : JsonSerializer.Deserialize<List<CommandConflict>>(row.ConflictsJson, JsonOptions),
        duplicate);

    // ------------------------------------------------------------------ helpers

    private async Task<SyncDeviceInfo> RequireActiveDeviceAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        SyncDeviceInfo device;
        await using (var cmd = TenantDb.Create(connection, null, $"SELECT {DeviceColumns} FROM core.sync_device WHERE id = @id;"))
        {
            TenantDb.Add(cmd, "id", NpgsqlDbType.Uuid, deviceId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            device = await reader.ReadAsync(cancellationToken)
                ? ReadDevice(reader)
                : throw new KeyNotFoundException($"Device '{deviceId}' was not found.");
        }

        if (device.UserId != RequireUser())
        {
            throw new UnauthorizedAccessException("The device belongs to another user.");
        }

        if (device.Status != "active")
        {
            throw new SyncDeviceBlockedException(
                $"Device '{device.Name}' is blocked{(string.IsNullOrWhiteSpace(device.BlockedReason) ? string.Empty : $": {device.BlockedReason}")}.");
        }

        await using var seen = TenantDb.Create(connection, null, "UPDATE core.sync_device SET last_seen_at = now() WHERE id = @id;");
        TenantDb.Add(seen, "id", NpgsqlDbType.Uuid, deviceId);
        await seen.ExecuteNonQueryAsync(cancellationToken);
        return device;
    }

    private async Task<ReservationInfo?> FindReservationAsync(DbConnection connection, string objectId, Guid recordId, CancellationToken cancellationToken)
    {
        await using var cmd = TenantDb.Create(
            connection,
            null,
            "SELECT id, device_id, object_id, record_id, expires_at FROM core.sync_reservation WHERE object_id = @object AND record_id = @record AND expires_at > now();");
        TenantDb.Add(cmd, "object", NpgsqlDbType.Varchar, objectId);
        TenantDb.Add(cmd, "record", NpgsqlDbType.Uuid, recordId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadReservation(reader) : null;
    }

    private async Task<IReadOnlyList<SyncSetDefinition>> LoadSetsAsync(CancellationToken cancellationToken)
    {
        var apps = await dbContext.InstalledApps.AsNoTracking().OrderBy(a => a.AppId).ToListAsync(cancellationToken);
        return apps
            .SelectMany(a => JsonSerializer.Deserialize<AppManifest>(a.ManifestJson, AppHostService.JsonOptions)?.SyncSets ?? [])
            .ToList();
    }

    private async Task<IReadOnlyList<CommandDefinition>> LoadCommandsAsync(CancellationToken cancellationToken)
    {
        var apps = await dbContext.InstalledApps.AsNoTracking().OrderBy(a => a.AppId).ToListAsync(cancellationToken);
        return apps
            .SelectMany(a => JsonSerializer.Deserialize<AppManifest>(a.ManifestJson, AppHostService.JsonOptions)?.Commands ?? [])
            .ToList();
    }

    private FilterNode? ParseFilter(string? expression) =>
        string.IsNullOrWhiteSpace(expression)
            ? null
            : FilterExpressionParser.Parse(
                expression
                    .Replace("{me}", RequireUser().ToString("D"), StringComparison.Ordinal)
                    .Replace("{today}", DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.Ordinal));

    private sealed record SnapshotState(string Head, int ObjectIndex, int Skip);

    private static string EncodeContinuation(SnapshotState state) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions));

    private static SnapshotState DecodeContinuation(string token)
    {
        try
        {
            return JsonSerializer.Deserialize<SnapshotState>(Convert.FromBase64String(token), JsonOptions)
                   ?? throw new FormatException();
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new RecordValidationException("The continuation token is not valid.", "continuation");
        }
    }

    private static (string Xid, long Seq) ParseCursor(string cursor)
    {
        var parts = cursor.Split('.');
        if (parts.Length != 2 || !ulong.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out _)
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var seq))
        {
            throw new RecordValidationException("The cursor is not valid; use the cursor returned by an earlier pull.", "cursor");
        }

        return (parts[0], seq);
    }

    private static SyncDeviceInfo ReadDevice(DbDataReader r) => new(
        r.GetGuid(0),
        r.GetGuid(1),
        r.GetString(2),
        r.GetString(3),
        r.IsDBNull(4) ? null : r.GetFieldValue<DateTimeOffset>(4),
        r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5),
        r.IsDBNull(6) ? null : r.GetString(6),
        r.GetFieldValue<DateTimeOffset>(7));

    private static ReservationInfo ReadReservation(DbDataReader r) =>
        new(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetGuid(3), r.GetFieldValue<DateTimeOffset>(4));

    private Guid RequireTenant() => tenantContext.TenantId
        ?? throw new InvalidOperationException("Sync requires a tenant context.");

    private Guid RequireUser() => tenantContext.UserId
        ?? throw new UnauthorizedAccessException("Sync requires a user.");
}
