using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Erp.Core.Domain.Data;
using Erp.Core.Domain.Metadata;
using Erp.Core.Domain.Tenancy;
using Erp.Core.Infrastructure.Apps;
using Erp.Core.Infrastructure.Events;
using Erp.Core.Infrastructure.Metadata;
using Erp.Core.Infrastructure.Persistence;
using Erp.Core.Infrastructure.Security;
using Erp.Core.Infrastructure.Workflow;
using Erp.Sdk.Contracts.V1;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Erp.Core.Infrastructure.Data;

/// <summary>
/// Generic metadata-driven Data API service supporting Get, Query (with filter tree),
/// Create, Update (with optimistic concurrency via <c>row_version</c>), Delete, Batch,
/// and Audit Trail inspection.
/// </summary>
public sealed class RecordDataService(
    CoreDbContext dbContext,
    MetadataRegistryService metadataRegistry,
    ITenantContext tenantContext,
    AppHostService? appHostService = null,
    ActiveCallTransactionRegistry? activeTxRegistry = null,
    EventPublisher? eventPublisher = null,
    WorkflowStore? workflowStore = null,
    WorkflowNotifier? workflowNotifier = null,
    PermissionService? permissions = null)
{
    public async Task<RecordData?> GetAsync(
        string objectId,
        Guid id,
        Guid? companyId = null,
        CancellationToken cancellationToken = default)
    {
        await RequirePermissionAsync(objectId, "read", cancellationToken);
        return await GetCoreAsync(objectId, id, companyId, cancellationToken);
    }

    private Task RequirePermissionAsync(string objectId, string action, CancellationToken cancellationToken) =>
        permissions is null ? Task.CompletedTask : permissions.RequireAsync(objectId, action, cancellationToken);

    private async Task<RecordData?> GetCoreAsync(
        string objectId,
        Guid id,
        Guid? companyId = null,
        CancellationToken cancellationToken = default)
    {
        var objectDef = await RequireObjectAsync(objectId, cancellationToken);
        var (schema, table) = objectDef.GetSchemaAndTable();

        var sql = new StringBuilder($"""SELECT * FROM "{schema}"."{table}" WHERE id = @id""");
        if (companyId.HasValue && objectDef.IsCompanyScoped)
        {
            sql.Append(" AND (company_id = @companyId OR company_id IS NULL)");
        }

        return await ExecuteWithConnectionAsync(async (conn, tx) =>
        {
            await using var cmd = CreateCommand(conn, tx, sql.ToString());
            AddParameter(cmd, "id", id);
            if (companyId.HasValue && objectDef.IsCompanyScoped)
            {
                AddParameter(cmd, "companyId", companyId.Value);
            }

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            return ReadRecord(reader, objectDef);
        }, cancellationToken);
    }

    public async Task<int> CountAsync(
        string objectId,
        FilterNode? filter,
        Guid? companyId = null,
        CancellationToken cancellationToken = default)
    {
        await RequirePermissionAsync(objectId, "read", cancellationToken);
        var objectDef = await RequireObjectAsync(objectId, cancellationToken);
        var (schema, table) = objectDef.GetSchemaAndTable();

        return await ExecuteWithConnectionAsync(async (conn, tx) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;

            var whereClauses = new List<string>();
            if (companyId.HasValue && objectDef.IsCompanyScoped)
            {
                whereClauses.Add("(company_id = @companyId OR company_id IS NULL)");
                AddParameter(cmd, "companyId", companyId.Value);
            }

            if (filter is not null)
            {
                var paramIndex = 0;
                whereClauses.Add(CompileFilterNode(filter, objectDef, cmd, ref paramIndex));
            }

            cmd.CommandText =
                $"""SELECT count(*) FROM "{schema}"."{table}" {(whereClauses.Count > 0 ? "WHERE " + string.Join(" AND ", whereClauses) : string.Empty)};""";
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }, cancellationToken);
    }

    private static readonly Regex AggregateAliasPattern = new("^[A-Za-z][A-Za-z0-9_]{0,40}$", RegexOptions.Compiled);

    /// <summary>
    /// Summaries (count, sum, avg, min, max) with optional grouping, over the same records and with the same permission,
    /// company and tenant rules as <see cref="QueryAsync"/> (ADR-0016). Each row has the group-by fields and the measure
    /// aliases as keys.
    /// </summary>
    public async Task<IReadOnlyList<Dictionary<string, object?>>> AggregateAsync(
        string objectId,
        AggregateQuery query,
        Guid? companyId = null,
        CancellationToken cancellationToken = default)
    {
        await RequirePermissionAsync(objectId, "read", cancellationToken);
        var objectDef = await RequireObjectAsync(objectId, cancellationToken);
        var (schema, table) = objectDef.GetSchemaAndTable();

        if (query.Measures.Count == 0)
        {
            throw new RecordValidationException("An aggregate query needs at least one measure.");
        }

        var groupBy = query.GroupBy ?? [];
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selectParts = new List<string>();
        var groupColumns = new List<string>();
        foreach (var group in groupBy)
        {
            var (column, type) = ResolveFilterTargetColumn(group, objectDef);
            if (type is FieldType.Json or FieldType.Text)
            {
                throw new RecordValidationException($"Cannot group by '{group}': {type} fields are not groupable.", group);
            }

            if (!AggregateAliasPattern.IsMatch(group.Replace(".", "_", StringComparison.Ordinal)) || !names.Add(group))
            {
                throw new RecordValidationException($"Invalid or duplicate group-by field '{group}'.", group);
            }

            groupColumns.Add($"\"{column}\"");
            selectParts.Add($"\"{column}\" AS \"{group}\"");
        }

        foreach (var measure in query.Measures)
        {
            var function = (measure.Function ?? string.Empty).Trim().ToLowerInvariant();
            if (function is not ("count" or "sum" or "avg" or "min" or "max"))
            {
                throw new RecordValidationException($"Unknown aggregate function '{measure.Function}'. Use count, sum, avg, min or max.");
            }

            if (!AggregateAliasPattern.IsMatch(measure.Alias ?? string.Empty) || !names.Add(measure.Alias!))
            {
                throw new RecordValidationException($"Measure alias '{measure.Alias}' must be unique and match [A-Za-z][A-Za-z0-9_]*.");
            }

            if (function == "count" && string.IsNullOrWhiteSpace(measure.Field))
            {
                selectParts.Add($"count(*) AS \"{measure.Alias}\"");
                continue;
            }

            if (string.IsNullOrWhiteSpace(measure.Field))
            {
                throw new RecordValidationException($"Aggregate '{function}' needs a field.", measure.Alias);
            }

            var (column, type) = ResolveFilterTargetColumn(measure.Field, objectDef);
            var numeric = type is FieldType.Integer or FieldType.Decimal or FieldType.Money;
            if (function is "sum" or "avg" && !numeric)
            {
                throw new RecordValidationException($"Cannot {function} '{measure.Field}': it is not a numeric field.", measure.Field);
            }

            if (function is "min" or "max" && type is FieldType.Json or FieldType.Blob or FieldType.Boolean)
            {
                throw new RecordValidationException($"Cannot {function} '{measure.Field}'.", measure.Field);
            }

            selectParts.Add($"{function}(\"{column}\") AS \"{measure.Alias}\"");
        }

        return await ExecuteWithConnectionAsync(async (conn, tx) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;

            var whereClauses = new List<string>();
            if (companyId.HasValue && objectDef.IsCompanyScoped)
            {
                whereClauses.Add("(company_id = @companyId OR company_id IS NULL)");
                AddParameter(cmd, "companyId", companyId.Value);
            }

            if (query.Filter is not null)
            {
                var paramIndex = 0;
                whereClauses.Add(CompileFilterNode(query.Filter, objectDef, cmd, ref paramIndex));
            }

            var sql = new StringBuilder($"""SELECT {string.Join(", ", selectParts)} FROM "{schema}"."{table}" """);
            if (whereClauses.Count > 0)
            {
                sql.Append("WHERE ").Append(string.Join(" AND ", whereClauses)).Append(' ');
            }

            if (groupColumns.Count > 0)
            {
                sql.Append("GROUP BY ").Append(string.Join(", ", groupColumns))
                    .Append(" ORDER BY ").Append(string.Join(", ", groupColumns)).Append(' ');
            }

            sql.Append($"LIMIT {Math.Clamp(query.Top <= 0 ? 1000 : query.Top, 1, 5000)};");
            cmd.CommandText = sql.ToString();

            var rows = new List<Dictionary<string, object?>>();
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row[reader.GetName(i)] = await reader.IsDBNullAsync(i, cancellationToken) ? null : reader.GetValue(i);
                }

                rows.Add(row);
            }

            return (IReadOnlyList<Dictionary<string, object?>>)rows;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<RecordData>> QueryAsync(
        string objectId,
        DataQuery query,
        Guid? companyId = null,
        CancellationToken cancellationToken = default)
    {
        await RequirePermissionAsync(objectId, "read", cancellationToken);
        var objectDef = await RequireObjectAsync(objectId, cancellationToken);
        var (schema, table) = objectDef.GetSchemaAndTable();

        return await ExecuteWithConnectionAsync(async (conn, tx) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;

            var whereClauses = new List<string>();
            if (companyId.HasValue && objectDef.IsCompanyScoped)
            {
                whereClauses.Add("(company_id = @companyId OR company_id IS NULL)");
                AddParameter(cmd, "companyId", companyId.Value);
            }

            if (query.Filter is not null)
            {
                var paramIndex = 0;
                var filterSql = CompileFilterNode(query.Filter, objectDef, cmd, ref paramIndex);
                whereClauses.Add(filterSql);
            }

            var sql = new StringBuilder($"""SELECT * FROM "{schema}"."{table}" """);
            if (whereClauses.Count > 0)
            {
                sql.Append("WHERE ").Append(string.Join(" AND ", whereClauses)).Append(' ');
            }

            if (query.OrderBy is { Count: > 0 })
            {
                var orderParts = new List<string>();
                foreach (var sort in query.OrderBy)
                {
                    var (col, _) = ResolveFilterTargetColumn(sort.Field, objectDef);
                    orderParts.Add($"\"{col}\" {(sort.Descending ? "DESC" : "ASC")}");
                }
                sql.Append("ORDER BY ").Append(string.Join(", ", orderParts)).Append(' ');
            }
            else
            {
                sql.Append("ORDER BY created_at DESC, id ASC ");
            }

            var top = Math.Clamp(query.Top <= 0 ? 50 : query.Top, 1, 1000);
            var skip = Math.Max(0, query.Skip);
            sql.Append($"LIMIT {top} OFFSET {skip};");

            cmd.CommandText = sql.ToString();

            var results = new List<RecordData>();
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var record = ReadRecord(reader, objectDef, query.Select);
                results.Add(record);
            }

            return (IReadOnlyList<RecordData>)results;
        }, cancellationToken);
    }

    public async Task<RecordData> CreateAsync(
        string objectId,
        IDictionary<string, object?> fields,
        Guid? companyId = null,
        Guid? explicitId = null,
        CancellationToken cancellationToken = default)
    {
        await RequirePermissionAsync(objectId, "create", cancellationToken);
        var objectDef = await RequireObjectAsync(objectId, cancellationToken);
        var (schema, table) = objectDef.GetSchemaAndTable();

        var tenantId = tenantContext.TenantId
            ?? throw new InvalidOperationException("Cannot create a record without an active TenantId in ITenantContext.");
        var userId = tenantContext.UserId ?? tenantId;
        var effectiveCompanyId = objectDef.IsCompanyScoped ? (companyId ?? tenantContext.CompanyId) : null;
        var recordId = explicitId is { } eid && eid != Guid.Empty ? eid : Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var correlationId = activeTxRegistry?.CurrentCorrelationId ?? Guid.NewGuid().ToString("D");

        ValidateUnknownFields(fields, objectDef);

        var createdRecord = await ExecuteWithConnectionAsync(async (conn, tx) =>
        {
            if (appHostService is not null)
            {
                await appHostService.InvokeSynchronousHooksAsync(
                    HookTypeProto.OnValidate,
                    objectDef.Id,
                    "create",
                    recordId,
                    1L,
                    fields,
                    effectiveCompanyId,
                    correlationId,
                    cancellationToken);

                await appHostService.InvokeSynchronousHooksAsync(
                    HookTypeProto.OnBeforeSave,
                    objectDef.Id,
                    "create",
                    recordId,
                    1L,
                    fields,
                    effectiveCompanyId,
                    correlationId,
                    cancellationToken);
            }

            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;

            var columnNames = new List<string>
            {
                "\"id\"",
                "\"tenant_id\"",
                "\"company_id\"",
                "\"created_at\"",
                "\"created_by\"",
                "\"updated_at\"",
                "\"updated_by\"",
                "\"row_version\""
            };

            var paramNames = new List<string>
            {
                "@id",
                "@tenant_id",
                "@company_id",
                "@created_at",
                "@created_by",
                "@updated_at",
                "@updated_by",
                "@row_version"
            };

            AddParameter(cmd, "id", recordId);
            AddParameter(cmd, "tenant_id", tenantId);
            AddParameter(cmd, "company_id", (object?)effectiveCompanyId ?? DBNull.Value);
            AddParameter(cmd, "created_at", now);
            AddParameter(cmd, "created_by", userId);
            AddParameter(cmd, "updated_at", now);
            AddParameter(cmd, "updated_by", userId);
            AddParameter(cmd, "row_version", 1L);

            var effectiveFields = BuildEffectiveFieldsForCreate(fields, objectDef);

            var pIdx = 0;
            foreach (var field in objectDef.Fields)
            {
                var hasValue = TryGetFieldInput(fields, field, objectDef.OwnerApp, out var rawValue);
                if (field.NumberSeries is not null)
                {
                    if (hasValue && !WhenRuleEvaluator.IsValueEmpty(rawValue))
                    {
                        throw new RecordValidationException(
                            $"Field '{field.Name}' on '{objectDef.Id}' is assigned by a number series and cannot be set.",
                            field.Name);
                    }

                    // Advanced in this transaction: a failed create rolls the counter back too, so the series has no gaps.
                    rawValue = await AllocateNumberAsync(conn, tx, objectDef, field, effectiveCompanyId, now, cancellationToken);
                    hasValue = true;
                }

                if (!hasValue || rawValue is null)
                {
                    if (field.DefaultValue is not null)
                    {
                        rawValue = field.DefaultValue;
                        hasValue = true;
                    }
                    else if (field.Required)
                    {
                        throw new RecordValidationException(
                            $"Field '{field.Name}' on '{objectDef.Id}' is required.",
                            field.Name);
                    }
                }

                if (field.RequiredWhen is not null
                    && WhenRuleEvaluator.Evaluate(field.RequiredWhen, effectiveFields)
                    && (!hasValue || WhenRuleEvaluator.IsValueEmpty(rawValue)))
                {
                    throw new RecordValidationException(
                        $"Field '{field.Name}' on '{objectDef.Id}' is required under the current conditions.",
                        field.Name);
                }

                if (!hasValue)
                {
                    continue;
                }

                pIdx = await BindFieldParametersAsync(
                    cmd,
                    field,
                    objectDef.OwnerApp,
                    rawValue,
                    pIdx,
                    (col, pName) =>
                    {
                        columnNames.Add($"\"{col}\"");
                        paramNames.Add(pName);
                    },
                    cancellationToken);
            }

            cmd.CommandText =
                $"""
                INSERT INTO "{schema}"."{table}" ({string.Join(", ", columnNames)})
                VALUES ({string.Join(", ", paramNames)})
                RETURNING *;
                """;

            RecordData saved;
            await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
            {
                await reader.ReadAsync(cancellationToken);
                saved = ReadRecord(reader, objectDef);
            }

            if (appHostService is not null)
            {
                await appHostService.InvokeSynchronousHooksAsync(
                    HookTypeProto.OnAfterSave,
                    objectDef.Id,
                    "create",
                    saved.Id,
                    saved.RowVersion,
                    saved.Fields,
                    effectiveCompanyId,
                    correlationId,
                    cancellationToken);
            }

            await PublishRecordEventAsync(
                conn, tx, $"{objectDef.Id}.created@1", objectDef.Id, saved.Id, saved.Fields, effectiveCompanyId, correlationId, cancellationToken);

            return saved;
        }, cancellationToken, correlationId);

        eventPublisher?.NotifyCommitted();
        return createdRecord;
    }

    public async Task<RecordData> UpdateAsync(
        string objectId,
        Guid id,
        long expectedRowVersion,
        IDictionary<string, object?> fields,
        Guid? companyId = null,
        CancellationToken cancellationToken = default)
    {
        await RequirePermissionAsync(objectId, "update", cancellationToken);
        var objectDef = await RequireObjectAsync(objectId, cancellationToken);
        var (schema, table) = objectDef.GetSchemaAndTable();
        var userId = tenantContext.UserId ?? tenantContext.TenantId ?? Guid.Empty;
        var now = DateTimeOffset.UtcNow;
        var correlationId = activeTxRegistry?.CurrentCorrelationId ?? Guid.NewGuid().ToString("D");

        ValidateUnknownFields(fields, objectDef);

        var updatedRecord = await ExecuteWithConnectionAsync(async (conn, tx) =>
        {
            if (objectDef.Status is not null
                || objectDef.Fields.Any(f => f.RequiredWhen is not null || f.ReadOnlyWhen is not null))
            {
                await ValidateConditionalRulesOnUpdateAsync(conn, tx, objectDef, id, fields, cancellationToken);
            }

            if (appHostService is not null)
            {
                await appHostService.InvokeSynchronousHooksAsync(
                    HookTypeProto.OnValidate,
                    objectDef.Id,
                    "update",
                    id,
                    expectedRowVersion,
                    fields,
                    companyId,
                    correlationId,
                    cancellationToken);

                await appHostService.InvokeSynchronousHooksAsync(
                    HookTypeProto.OnBeforeSave,
                    objectDef.Id,
                    "update",
                    id,
                    expectedRowVersion,
                    fields,
                    companyId,
                    correlationId,
                    cancellationToken);
            }

            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;

            var assignments = new List<string>
            {
                "\"updated_at\" = @updated_at",
                "\"updated_by\" = @updated_by",
                "\"row_version\" = \"row_version\" + 1"
            };

            AddParameter(cmd, "id", id);
            AddParameter(cmd, "expected_row_version", expectedRowVersion);
            AddParameter(cmd, "updated_at", now);
            AddParameter(cmd, "updated_by", userId);

            var pIdx = 0;
            foreach (var field in objectDef.Fields)
            {
                if (!TryGetFieldInput(fields, field, objectDef.OwnerApp, out var rawValue))
                {
                    continue;
                }

                if (field.NumberSeries is not null)
                {
                    throw new RecordValidationException(
                        $"Field '{field.Name}' on '{objectDef.Id}' is assigned by a number series and cannot be changed.",
                        field.Name);
                }

                if (rawValue is null && field.Required)
                {
                    throw new RecordValidationException(
                        $"Required field '{field.Name}' on '{objectDef.Id}' cannot be set to null.",
                        field.Name);
                }

                pIdx = await BindFieldParametersAsync(
                    cmd,
                    field,
                    objectDef.OwnerApp,
                    rawValue,
                    pIdx,
                    (col, pName) => assignments.Add($"\"{col}\" = {pName}"),
                    cancellationToken);
            }

            var companyClause = companyId.HasValue && objectDef.IsCompanyScoped
                ? " AND (company_id = @companyId OR company_id IS NULL)"
                : string.Empty;

            if (companyId.HasValue && objectDef.IsCompanyScoped)
            {
                AddParameter(cmd, "companyId", companyId.Value);
            }

            cmd.CommandText =
                $"""
                UPDATE "{schema}"."{table}"
                   SET {string.Join(", ", assignments)}
                 WHERE id = @id
                   AND row_version = @expected_row_version{companyClause}
                RETURNING *;
                """;

            RecordData? saved = null;
            await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
            {
                if (await reader.ReadAsync(cancellationToken))
                {
                    saved = ReadRecord(reader, objectDef);
                }
            }

            if (saved is not null)
            {
                if (appHostService is not null)
                {
                    await appHostService.InvokeSynchronousHooksAsync(
                        HookTypeProto.OnAfterSave,
                        objectDef.Id,
                        "update",
                        saved.Id,
                        saved.RowVersion,
                        saved.Fields,
                        companyId,
                        correlationId,
                        cancellationToken);
                }

                await PublishRecordEventAsync(
                    conn, tx, $"{objectDef.Id}.updated@1", objectDef.Id, saved.Id, saved.Fields, companyId, correlationId, cancellationToken);

                return saved;
            }

            // Distinguish between 404 Not Found and 412 Concurrency Conflict
            await ThrowNotFoundOrConcurrencyConflictAsync(conn, tx, schema, table, objectDef.Id, id, expectedRowVersion, cancellationToken);
            throw new RecordNotFoundException(objectDef.Id, id);
        }, cancellationToken, correlationId);

        eventPublisher?.NotifyCommitted();
        return updatedRecord;
    }

    public async Task<RecordData> TransitionStatusAsync(
        string objectId,
        Guid id,
        string transitionName,
        long? expectedRowVersion = null,
        Guid? companyId = null,
        CancellationToken cancellationToken = default)
    {
        var objectDef = await RequireObjectAsync(objectId, cancellationToken);
        if (objectDef.Status is null
            || !objectDef.Status.Transitions.TryGetValue(transitionName, out var transition))
        {
            throw new RecordValidationException(
                $"Object '{objectDef.Id}' does not define status transition '{transitionName}'.");
        }

        await RequirePermissionAsync(
            objectDef.Id, string.IsNullOrWhiteSpace(transition.Permission) ? "update" : transition.Permission, cancellationToken);

        var existing = await GetCoreAsync(objectId, id, companyId, cancellationToken)
            ?? throw new RecordNotFoundException(objectId, id);

        var statusFieldName = string.IsNullOrWhiteSpace(objectDef.Status.Field) ? "status" : objectDef.Status.Field;
        existing.Fields.TryGetValue(statusFieldName, out var currentStatusObj);
        var currentStatus = Convert.ToString(currentStatusObj, CultureInfo.InvariantCulture) ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(transition.From)
            && !string.Equals(currentStatus, transition.From, StringComparison.OrdinalIgnoreCase))
        {
            throw new RecordValidationException(
                $"Cannot execute transition '{transitionName}' on '{objectDef.Id}' from status '{currentStatus}' (expected '{transition.From}').",
                statusFieldName);
        }

        var rowVer = expectedRowVersion ?? existing.RowVersion;
        var correlationId = activeTxRegistry?.CurrentCorrelationId ?? Guid.NewGuid().ToString("D");
        IReadOnlyList<Guid> openedTasks = [];

        var updatedRecord = await ExecuteWithConnectionAsync(async (conn, tx) =>
        {
            if (workflowStore is not null
                && await workflowStore.HasRunningInstanceAsync(conn, tx, objectDef.Id, id, cancellationToken))
            {
                throw new RecordValidationException(
                    $"Record '{id}' on '{objectDef.Id}' is locked by a running workflow; progress it through its workflow tasks.",
                    statusFieldName);
            }

            var changed = await ApplyStatusChangeAsync(conn, tx, objectDef, id, rowVer, transition.To, cancellationToken);
            await PublishRecordEventAsync(
                conn, tx, $"{objectDef.Id}.updated@1", objectDef.Id, changed.Id, changed.Fields, changed.CompanyId, correlationId, cancellationToken);

            if (!string.IsNullOrWhiteSpace(transition.Workflow))
            {
                if (workflowStore is null)
                {
                    throw new RecordValidationException(
                        $"Transition '{transitionName}' on '{objectDef.Id}' requires workflow '{transition.Workflow}', but workflows are not available.");
                }

                var start = await workflowStore.StartAsync(
                    conn, tx, objectDef, changed, transitionName, transition.Workflow, changed.CompanyId, correlationId, cancellationToken);
                openedTasks = start.OpenedTaskIds;

                if (start.NoStepsApply && !string.IsNullOrWhiteSpace(start.Definition.OnComplete))
                {
                    changed = await ApplyWorkflowOutcomeAsync(
                        conn, tx, objectDef.Id, id, start.Definition.OnComplete, correlationId, cancellationToken);
                }
            }

            return changed;
        }, cancellationToken, correlationId);

        eventPublisher?.NotifyCommitted();
        if (workflowNotifier is not null && openedTasks.Count > 0)
        {
            await workflowNotifier.NotifyTasksAsync(openedTasks, cancellationToken);
        }

        return updatedRecord;
    }

    /// <summary>
    /// Executes a status transition on behalf of the workflow engine, inside the caller's transaction. Bypasses the
    /// workflow lock (the engine is what releases it) and verifies the record is still in the transition's source status.
    /// </summary>
    public async Task<RecordData> ApplyWorkflowOutcomeAsync(
        DbConnection conn,
        DbTransaction? tx,
        string objectId,
        Guid recordId,
        string transitionName,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var objectDef = await RequireObjectAsync(objectId, cancellationToken);
        if (objectDef.Status is null
            || !objectDef.Status.Transitions.TryGetValue(transitionName, out var transition))
        {
            throw new RecordValidationException($"Object '{objectDef.Id}' does not define status transition '{transitionName}'.");
        }

        var (schema, table) = objectDef.GetSchemaAndTable();
        RecordData current;
        await using (var cmd = CreateCommand(conn, tx, $"""SELECT * FROM "{schema}"."{table}" WHERE id = @id FOR UPDATE;"""))
        {
            AddParameter(cmd, "id", recordId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            current = await reader.ReadAsync(cancellationToken)
                ? ReadRecord(reader, objectDef)
                : throw new RecordNotFoundException(objectDef.Id, recordId);
        }

        var statusFieldName = string.IsNullOrWhiteSpace(objectDef.Status.Field) ? "status" : objectDef.Status.Field;
        current.Fields.TryGetValue(statusFieldName, out var statusValue);
        var currentStatus = Convert.ToString(statusValue, CultureInfo.InvariantCulture) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(transition.From)
            && !string.Equals(currentStatus, transition.From, StringComparison.OrdinalIgnoreCase))
        {
            throw new RecordValidationException(
                $"Cannot execute transition '{transitionName}' on '{objectDef.Id}' from status '{currentStatus}' (expected '{transition.From}').",
                statusFieldName);
        }

        var changed = await ApplyStatusChangeAsync(conn, tx, objectDef, recordId, current.RowVersion, transition.To, cancellationToken);
        await PublishRecordEventAsync(
            conn, tx, $"{objectDef.Id}.updated@1", objectDef.Id, changed.Id, changed.Fields, changed.CompanyId, correlationId, cancellationToken);
        return changed;
    }

    private async Task<RecordData> ApplyStatusChangeAsync(
        DbConnection conn,
        DbTransaction? tx,
        ObjectDefinition objectDef,
        Guid id,
        long rowVersion,
        string newStatus,
        CancellationToken cancellationToken)
    {
        var (schema, table) = objectDef.GetSchemaAndTable();
        var statusFieldName = string.IsNullOrWhiteSpace(objectDef.Status?.Field) ? "status" : objectDef.Status!.Field;
        var statusFieldDef = objectDef.Fields.FirstOrDefault(
            f => string.Equals(f.Name, statusFieldName, StringComparison.OrdinalIgnoreCase));
        var statusCol = statusFieldDef?.GetColumnName(objectDef.OwnerApp)
            ?? IdentifierNaming.ToSnakeCase(statusFieldName);
        var userId = tenantContext.UserId ?? tenantContext.TenantId ?? Guid.Empty;

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;

        AddParameter(cmd, "id", id);
        AddParameter(cmd, "expected_row_version", rowVersion);
        AddParameter(cmd, "new_status", newStatus);
        AddParameter(cmd, "updated_at", DateTimeOffset.UtcNow);
        AddParameter(cmd, "updated_by", userId);

        cmd.CommandText =
            $"""
            UPDATE "{schema}"."{table}"
               SET "{statusCol}" = @new_status,
                   "updated_at" = @updated_at,
                   "updated_by" = @updated_by,
                   "row_version" = "row_version" + 1
             WHERE id = @id
               AND row_version = @expected_row_version
            RETURNING *;
            """;

        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                return ReadRecord(reader, objectDef);
            }
        }

        await ThrowNotFoundOrConcurrencyConflictAsync(conn, tx, schema, table, objectDef.Id, id, rowVersion, cancellationToken);
        throw new RecordNotFoundException(objectDef.Id, id);
    }

    public async Task DeleteAsync(
        string objectId,
        Guid id,
        long expectedRowVersion,
        Guid? companyId = null,
        CancellationToken cancellationToken = default)
    {
        await RequirePermissionAsync(objectId, "delete", cancellationToken);
        var objectDef = await RequireObjectAsync(objectId, cancellationToken);
        var (schema, table) = objectDef.GetSchemaAndTable();
        var correlationId = activeTxRegistry?.CurrentCorrelationId ?? Guid.NewGuid().ToString("D");

        await ExecuteWithConnectionAsync<object?>(async (conn, tx) =>
        {
            if (workflowStore is not null
                && await workflowStore.HasRunningInstanceAsync(conn, tx, objectDef.Id, id, cancellationToken))
            {
                throw new RecordValidationException(
                    $"Record '{id}' on '{objectDef.Id}' is locked by a running workflow and cannot be deleted.");
            }

            if (appHostService is not null)
            {
                var emptyFields = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                await appHostService.InvokeSynchronousHooksAsync(
                    HookTypeProto.OnValidate,
                    objectDef.Id,
                    "delete",
                    id,
                    expectedRowVersion,
                    emptyFields,
                    companyId,
                    correlationId,
                    cancellationToken);

                await appHostService.InvokeSynchronousHooksAsync(
                    HookTypeProto.OnBeforeSave,
                    objectDef.Id,
                    "delete",
                    id,
                    expectedRowVersion,
                    emptyFields,
                    companyId,
                    correlationId,
                    cancellationToken);
            }

            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;

            AddParameter(cmd, "id", id);
            AddParameter(cmd, "expected_row_version", expectedRowVersion);

            var companyClause = companyId.HasValue && objectDef.IsCompanyScoped
                ? " AND (company_id = @companyId OR company_id IS NULL)"
                : string.Empty;

            if (companyId.HasValue && objectDef.IsCompanyScoped)
            {
                AddParameter(cmd, "companyId", companyId.Value);
            }

            cmd.CommandText =
                $"""
                DELETE FROM "{schema}"."{table}"
                 WHERE id = @id
                   AND row_version = @expected_row_version{companyClause};
                """;

            var affected = await cmd.ExecuteNonQueryAsync(cancellationToken);
            if (affected == 0)
            {
                await ThrowNotFoundOrConcurrencyConflictAsync(conn, tx, schema, table, objectDef.Id, id, expectedRowVersion, cancellationToken);
            }

            if (appHostService is not null)
            {
                await appHostService.InvokeSynchronousHooksAsync(
                    HookTypeProto.OnAfterSave,
                    objectDef.Id,
                    "delete",
                    id,
                    expectedRowVersion,
                    new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase),
                    companyId,
                    correlationId,
                    cancellationToken);
            }

            await PublishRecordEventAsync(
                conn, tx, $"{objectDef.Id}.deleted@1", objectDef.Id, id, null, companyId, correlationId, cancellationToken);

            return null;
        }, cancellationToken, correlationId);

        eventPublisher?.NotifyCommitted();
    }

    public async Task<int> DeleteWhereAsync(
        string objectId,
        string? filterExpression,
        Guid? companyId = null,
        CancellationToken cancellationToken = default)
    {
        var filterNode = string.IsNullOrWhiteSpace(filterExpression)
            ? null
            : FilterExpressionParser.Parse(filterExpression);

        var matching = await QueryAsync(
            objectId,
            new DataQuery { Filter = filterNode, Top = 1000 },
            companyId,
            cancellationToken);

        var deleted = 0;
        foreach (var rec in matching)
        {
            await DeleteAsync(objectId, rec.Id, rec.RowVersion, companyId, cancellationToken);
            deleted++;
        }

        return deleted;
    }

    public async Task<IReadOnlyList<BatchResultItem>> BatchAsync(
        IReadOnlyList<BatchOperation> operations,
        Guid? companyId = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<BatchResultItem>(operations.Count);

        await using var tx = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            for (var i = 0; i < operations.Count; i++)
            {
                var op = operations[i];
                switch (op.Method)
                {
                    case BatchMethod.Create:
                    {
                        var created = await CreateAsync(
                            op.ObjectId,
                            op.Fields ?? new Dictionary<string, object?>(),
                            companyId,
                            op.Id,
                            cancellationToken);
                        results.Add(new BatchResultItem(i, BatchMethod.Create, op.ObjectId, created.Id, created));
                        break;
                    }
                    case BatchMethod.Update:
                    {
                        if (!op.Id.HasValue)
                        {
                            throw new RecordValidationException($"Batch Update at index {i} requires 'id'.");
                        }
                        if (!op.RowVersion.HasValue)
                        {
                            throw new RecordValidationException($"Batch Update at index {i} requires 'rowVersion'.");
                        }

                        var updated = await UpdateAsync(
                            op.ObjectId,
                            op.Id.Value,
                            op.RowVersion.Value,
                            op.Fields ?? new Dictionary<string, object?>(),
                            companyId,
                            cancellationToken);
                        results.Add(new BatchResultItem(i, BatchMethod.Update, op.ObjectId, updated.Id, updated));
                        break;
                    }
                    case BatchMethod.Delete:
                    {
                        if (!op.Id.HasValue)
                        {
                            throw new RecordValidationException($"Batch Delete at index {i} requires 'id'.");
                        }
                        if (!op.RowVersion.HasValue)
                        {
                            throw new RecordValidationException($"Batch Delete at index {i} requires 'rowVersion'.");
                        }

                        await DeleteAsync(op.ObjectId, op.Id.Value, op.RowVersion.Value, companyId, cancellationToken);
                        results.Add(new BatchResultItem(i, BatchMethod.Delete, op.ObjectId, op.Id.Value));
                        break;
                    }
                    default:
                        throw new RecordValidationException($"Unsupported batch method '{op.Method}'.");
                }
            }

            await tx.CommitAsync(cancellationToken);
            return results;
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyList<AuditEntry>> GetAuditTrailAsync(
        string objectId,
        Guid recordId,
        CancellationToken cancellationToken = default)
    {
        await RequirePermissionAsync(objectId, "read", cancellationToken);
        var objectDef = await RequireObjectAsync(objectId, cancellationToken);
        var (schema, _) = objectDef.GetSchemaAndTable();
        var auditTable = objectDef.GetAuditTableName();

        return await ExecuteWithConnectionAsync(async (conn, tx) =>
        {
            await using var cmd = CreateCommand(
                conn,
                tx,
                $"""
                SELECT audit_id, record_id, tenant_id, operation, old_data, new_data, changed_by, changed_at, reason
                  FROM "{schema}"."{auditTable}"
                 WHERE record_id = @recordId
                 ORDER BY audit_id ASC;
                """);
            AddParameter(cmd, "recordId", recordId);

            var entries = new List<AuditEntry>();
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var oldJson = reader.IsDBNull(4) ? null : reader.GetString(4);
                var newJson = reader.IsDBNull(5) ? null : reader.GetString(5);

                entries.Add(new AuditEntry(
                    AuditId: reader.GetInt64(0),
                    RecordId: reader.GetGuid(1),
                    TenantId: reader.GetGuid(2),
                    Operation: reader.GetString(3),
                    OldData: oldJson is null ? null : JsonSerializer.Deserialize<Dictionary<string, object?>>(oldJson),
                    NewData: newJson is null ? null : JsonSerializer.Deserialize<Dictionary<string, object?>>(newJson),
                    ChangedBy: reader.GetGuid(6),
                    ChangedAt: reader.GetFieldValue<DateTimeOffset>(7),
                    Reason: reader.IsDBNull(8) ? null : reader.GetString(8)));
            }

            return (IReadOnlyList<AuditEntry>)entries;
        }, cancellationToken);
    }

    private async Task<ObjectDefinition> RequireObjectAsync(string objectId, CancellationToken cancellationToken)
        => await metadataRegistry.GetObjectAsync(objectId, cancellationToken)
           ?? throw new ObjectNotFoundException(objectId);

    private static async Task ThrowNotFoundOrConcurrencyConflictAsync(
        DbConnection conn,
        DbTransaction? tx,
        string schema,
        string table,
        string objectId,
        Guid id,
        long expectedRowVersion,
        CancellationToken cancellationToken)
    {
        await using var checkCmd = CreateCommand(
            conn,
            tx,
            $"""SELECT row_version FROM "{schema}"."{table}" WHERE id = @id;""");
        AddParameter(checkCmd, "id", id);

        var currentVersionObj = await checkCmd.ExecuteScalarAsync(cancellationToken);
        if (currentVersionObj is not null and not DBNull)
        {
            throw new ConcurrencyConflictException(objectId, id, expectedRowVersion);
        }

        throw new RecordNotFoundException(objectId, id);
    }

    private async Task<int> BindFieldParametersAsync(
        DbCommand cmd,
        FieldDefinition field,
        string objectOwnerApp,
        object? rawValue,
        int pIdx,
        Action<string, string> onColumnBound,
        CancellationToken cancellationToken)
    {
        var colName = field.GetColumnName(objectOwnerApp);

        if (field.Type == FieldType.Money)
        {
            var amountCol = $"{colName}_amount";
            var currencyCol = $"{colName}_currency";
            var amountParam = $"@p{pIdx++}";
            var currencyParam = $"@p{pIdx++}";

            if (rawValue is null)
            {
                AddParameter(cmd, amountParam, DBNull.Value);
                AddParameter(cmd, currencyParam, DBNull.Value);
            }
            else
            {
                var money = CoerceToMoney(rawValue, field.Name);
                ValidateNumericRange(field, money.Amount);
                AddParameter(cmd, amountParam, money.Amount);
                AddParameter(cmd, currencyParam, money.Currency);
            }

            onColumnBound(amountCol, amountParam);
            onColumnBound(currencyCol, currencyParam);
            return pIdx;
        }

        var paramName = $"@p{pIdx++}";
        if (rawValue is null)
        {
            AddParameter(cmd, paramName, DBNull.Value);
            onColumnBound(colName, paramName);
            return pIdx;
        }

        var typedValue = await CoerceAndValidateScalarAsync(field, rawValue, cancellationToken);
        if (field.Type == FieldType.Json && cmd is NpgsqlCommand npgsqlCmd)
        {
            npgsqlCmd.Parameters.Add(new NpgsqlParameter(paramName, NpgsqlDbType.Jsonb)
            {
                Value = typedValue
            });
        }
        else
        {
            AddParameter(cmd, paramName, typedValue);
        }

        onColumnBound(colName, paramName);
        return pIdx;
    }

    private async Task<object> CoerceAndValidateScalarAsync(
        FieldDefinition field,
        object rawValue,
        CancellationToken cancellationToken)
    {
        var unwrapped = UnwrapJsonElement(rawValue);
        if (unwrapped is null)
        {
            return DBNull.Value;
        }

        switch (field.Type)
        {
            case FieldType.String:
            {
                var str = Convert.ToString(unwrapped, CultureInfo.InvariantCulture) ?? string.Empty;
                var maxLen = field.MaxLength ?? 255;
                if (str.Length > maxLen)
                {
                    throw new RecordValidationException(
                        $"Field '{field.Name}' exceeds maximum length of {maxLen}.",
                        field.Name);
                }
                ValidatePattern(field, str);
                return str;
            }
            case FieldType.Text:
            {
                var str = Convert.ToString(unwrapped, CultureInfo.InvariantCulture) ?? string.Empty;
                if (field.MaxLength.HasValue && str.Length > field.MaxLength.Value)
                {
                    throw new RecordValidationException(
                        $"Field '{field.Name}' exceeds maximum length of {field.MaxLength.Value}.",
                        field.Name);
                }
                ValidatePattern(field, str);
                return str;
            }

            case FieldType.Integer:
            {
                long intVal;
                if (unwrapped is long l) intVal = l;
                else if (unwrapped is int i) intVal = i;
                else if (!long.TryParse(Convert.ToString(unwrapped, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out intVal))
                {
                    throw new RecordValidationException($"Field '{field.Name}' must be an integer.", field.Name);
                }
                ValidateNumericRange(field, intVal);
                return intVal;
            }

            case FieldType.Decimal:
            {
                decimal dec;
                if (unwrapped is decimal d) dec = d;
                else if (unwrapped is double dbl) dec = (decimal)dbl;
                else if (unwrapped is long dl) dec = dl;
                else if (unwrapped is int di) dec = di;
                else if (!decimal.TryParse(Convert.ToString(unwrapped, CultureInfo.InvariantCulture), NumberStyles.Number, CultureInfo.InvariantCulture, out dec))
                {
                    throw new RecordValidationException($"Field '{field.Name}' must be a decimal number.", field.Name);
                }

                ValidateNumericRange(field, dec);
                var scale = Math.Clamp(field.Scale ?? 2, 0, 10);
                return Math.Round(dec, scale, MidpointRounding.AwayFromZero);
            }

            case FieldType.Boolean:
                if (unwrapped is bool b) return b;
                if (bool.TryParse(Convert.ToString(unwrapped, CultureInfo.InvariantCulture), out var parsedBool))
                {
                    return parsedBool;
                }
                throw new RecordValidationException($"Field '{field.Name}' must be a boolean.", field.Name);

            case FieldType.Date:
                if (unwrapped is DateOnly dateOnly) return dateOnly;
                if (unwrapped is DateTime dt) return DateOnly.FromDateTime(dt);
                if (DateOnly.TryParse(Convert.ToString(unwrapped, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
                {
                    return parsedDate;
                }
                throw new RecordValidationException($"Field '{field.Name}' must be a valid ISO date (YYYY-MM-DD).", field.Name);

            case FieldType.DateTime:
                if (unwrapped is DateTimeOffset dto) return dto.ToUniversalTime();
                if (unwrapped is DateTime dtVal) return new DateTimeOffset(DateTime.SpecifyKind(dtVal, DateTimeKind.Utc));
                if (DateTimeOffset.TryParse(Convert.ToString(unwrapped, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedDto))
                {
                    return parsedDto.ToUniversalTime();
                }
                throw new RecordValidationException($"Field '{field.Name}' must be a valid ISO datetime.", field.Name);

            case FieldType.Uuid:
            case FieldType.Reference:
            case FieldType.Blob:
                if (unwrapped is Guid g) return g;
                if (Guid.TryParse(Convert.ToString(unwrapped, CultureInfo.InvariantCulture), out var parsedGuid))
                {
                    return parsedGuid;
                }
                throw new RecordValidationException($"Field '{field.Name}' must be a valid UUID.", field.Name);

            case FieldType.Enum:
            {
                var valStr = Convert.ToString(unwrapped, CultureInfo.InvariantCulture) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(field.EnumType))
                {
                    var enumDef = await metadataRegistry.GetEnumAsync(field.EnumType, cancellationToken);
                    if (enumDef is not null && !enumDef.Values.Contains(valStr, StringComparer.Ordinal))
                    {
                        throw new RecordValidationException(
                            $"Invalid value '{valStr}' for enum field '{field.Name}' (allowed: {string.Join(", ", enumDef.Values)}).",
                            field.Name);
                    }
                }
                return valStr;
            }

            case FieldType.Json:
                return rawValue is JsonElement je
                    ? je.GetRawText()
                    : rawValue is string s
                        ? s
                        : JsonSerializer.Serialize(rawValue);

            default:
                throw new RecordValidationException($"Unsupported field type '{field.Type}' on '{field.Name}'.", field.Name);
        }
    }

    private static MoneyValue CoerceToMoney(object rawValue, string fieldName)
    {
        if (rawValue is MoneyValue mv)
        {
            return mv;
        }

        if (rawValue is JsonElement je && je.ValueKind == JsonValueKind.Object)
        {
            var amountProp = je.TryGetProperty("amount", out var a) ? a : je.GetProperty("Amount");
            var currProp = je.TryGetProperty("currency", out var c) ? c : je.GetProperty("Currency");
            var amount = amountProp.GetDecimal();
            var currency = currProp.GetString() ?? string.Empty;
            if (currency.Length != 3)
            {
                throw new RecordValidationException($"Money field '{fieldName}' requires a 3-letter currency code.", fieldName);
            }
            return new MoneyValue(amount, currency.ToUpperInvariant());
        }

        if (rawValue is IDictionary<string, object?> dict)
        {
            var amountObj = dict.TryGetValue("amount", out var a) ? a : dict.TryGetValue("Amount", out var a2) ? a2 : null;
            var currObj = dict.TryGetValue("currency", out var c) ? c : dict.TryGetValue("Currency", out var c2) ? c2 : null;
            var amount = Convert.ToDecimal(UnwrapJsonElement(amountObj), CultureInfo.InvariantCulture);
            var currency = Convert.ToString(UnwrapJsonElement(currObj), CultureInfo.InvariantCulture) ?? string.Empty;
            if (currency.Length != 3)
            {
                throw new RecordValidationException($"Money field '{fieldName}' requires a 3-letter currency code.", fieldName);
            }
            return new MoneyValue(amount, currency.ToUpperInvariant());
        }

        throw new RecordValidationException($"Money field '{fieldName}' must be an object with 'amount' and 'currency'.", fieldName);
    }

    private static object? UnwrapJsonElement(object? value)
    {
        if (value is not JsonElement je)
        {
            return value;
        }

        return je.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => je.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => je.TryGetInt64(out var l) ? l : je.GetDecimal(),
            _ => je
        };
    }

    private static string CompileFilterNode(
        FilterNode node,
        ObjectDefinition objectDef,
        DbCommand cmd,
        ref int paramIndex)
    {
        switch (node)
        {
            case FilterGroup group:
            {
                if (group.Clauses.Count == 0)
                {
                    return "TRUE";
                }

                var op = group.Logical == LogicalOperator.Or ? " OR " : " AND ";
                var parts = new List<string>(group.Clauses.Count);
                foreach (var child in group.Clauses)
                {
                    parts.Add(CompileFilterNode(child, objectDef, cmd, ref paramIndex));
                }
                return $"({string.Join(op, parts)})";
            }

            case FilterCondition cond:
            {
                var (colName, fieldType) = ResolveFilterTargetColumn(cond.Field, objectDef);
                var quotedCol = $"\"{colName}\"";

                if (cond.Operator == FilterOperator.IsEmpty)
                {
                    return fieldType is FieldType.String or FieldType.Text or FieldType.Enum
                        ? $"({quotedCol} IS NULL OR {quotedCol} = '')"
                        : $"{quotedCol} IS NULL";
                }

                if (cond.Value is null)
                {
                    return cond.Operator switch
                    {
                        FilterOperator.Eq => $"{quotedCol} IS NULL",
                        FilterOperator.Ne => $"{quotedCol} IS NOT NULL",
                        _ => throw new RecordValidationException($"Null filter value is only valid with 'eq' or 'ne' on '{cond.Field}'.")
                    };
                }

                if (cond.Operator == FilterOperator.In)
                {
                    if (cond.Value is not IEnumerable<object?> list)
                    {
                        throw new RecordValidationException($"Operator 'in' on '{cond.Field}' requires a list of values.");
                    }

                    var pNames = new List<string>();
                    foreach (var item in list)
                    {
                        var pName = $"@f{paramIndex++}";
                        AddParameter(cmd, pName, CoerceFilterParameter(item, fieldType, cond.Field));
                        pNames.Add(pName);
                    }

                    return pNames.Count == 0 ? "FALSE" : $"{quotedCol} IN ({string.Join(", ", pNames)})";
                }

                var paramName = $"@f{paramIndex++}";
                if (cond.Operator == FilterOperator.Contains)
                {
                    var text = Convert.ToString(UnwrapJsonElement(cond.Value), CultureInfo.InvariantCulture) ?? string.Empty;
                    AddParameter(cmd, paramName, $"%{text}%");
                    return $"{quotedCol} ILIKE {paramName}";
                }

                AddParameter(cmd, paramName, CoerceFilterParameter(cond.Value, fieldType, cond.Field));
                var sqlOp = cond.Operator switch
                {
                    FilterOperator.Eq => "=",
                    FilterOperator.Ne => "<>",
                    FilterOperator.Gt => ">",
                    FilterOperator.Ge => ">=",
                    FilterOperator.Lt => "<",
                    FilterOperator.Le => "<=",
                    _ => throw new RecordValidationException($"Unsupported operator '{cond.Operator}'.")
                };

                return $"{quotedCol} {sqlOp} {paramName}";
            }

            default:
                throw new RecordValidationException($"Unknown filter node type '{node.GetType().Name}'.");
        }
    }

    private static (string ColumnName, FieldType Type) ResolveFilterTargetColumn(string fieldPath, ObjectDefinition objectDef)
    {
        var normalized = fieldPath.Trim();
        switch (normalized.ToLowerInvariant())
        {
            case "id": return ("id", FieldType.Uuid);
            case "tenantid" or "tenant_id": return ("tenant_id", FieldType.Uuid);
            case "companyid" or "company_id": return ("company_id", FieldType.Uuid);
            case "createdat" or "created_at": return ("created_at", FieldType.DateTime);
            case "createdby" or "created_by": return ("created_by", FieldType.Uuid);
            case "updatedat" or "updated_at": return ("updated_at", FieldType.DateTime);
            case "updatedby" or "updated_by": return ("updated_by", FieldType.Uuid);
            case "rowversion" or "row_version": return ("row_version", FieldType.Integer);
        }

        // Support filtering on money sub-properties, e.g. 'price.amount' or 'price.currency'
        if (normalized.EndsWith(".amount", StringComparison.OrdinalIgnoreCase))
        {
            var baseName = normalized[..^7];
            var moneyField = FindField(objectDef, baseName);
            if (moneyField is { Type: FieldType.Money })
            {
                return ($"{moneyField.GetColumnName(objectDef.OwnerApp)}_amount", FieldType.Decimal);
            }
        }
        else if (normalized.EndsWith(".currency", StringComparison.OrdinalIgnoreCase))
        {
            var baseName = normalized[..^9];
            var moneyField = FindField(objectDef, baseName);
            if (moneyField is { Type: FieldType.Money })
            {
                return ($"{moneyField.GetColumnName(objectDef.OwnerApp)}_currency", FieldType.String);
            }
        }

        var field = FindField(objectDef, normalized)
            ?? throw new RecordValidationException($"Unknown field '{fieldPath}' on object '{objectDef.Id}'.", fieldPath);

        if (field.Type == FieldType.Money)
        {
            return ($"{field.GetColumnName(objectDef.OwnerApp)}_amount", FieldType.Decimal);
        }

        return (field.GetColumnName(objectDef.OwnerApp), field.Type);
    }

    private static object CoerceFilterParameter(object? rawValue, FieldType type, string fieldName)
    {
        var unwrapped = UnwrapJsonElement(rawValue)
            ?? throw new RecordValidationException($"Filter value for '{fieldName}' cannot be null.", fieldName);

        return type switch
        {
            FieldType.Uuid or FieldType.Reference or FieldType.Blob =>
                unwrapped is Guid g ? g : Guid.Parse(Convert.ToString(unwrapped, CultureInfo.InvariantCulture)!),
            FieldType.Integer =>
                Convert.ToInt64(unwrapped, CultureInfo.InvariantCulture),
            FieldType.Decimal =>
                Convert.ToDecimal(unwrapped, CultureInfo.InvariantCulture),
            FieldType.Boolean =>
                Convert.ToBoolean(unwrapped, CultureInfo.InvariantCulture),
            FieldType.Date =>
                unwrapped is DateOnly d ? d : DateOnly.Parse(Convert.ToString(unwrapped, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture),
            FieldType.DateTime =>
                unwrapped is DateTimeOffset dto ? dto.ToUniversalTime() : DateTimeOffset.Parse(Convert.ToString(unwrapped, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime(),
            _ => Convert.ToString(unwrapped, CultureInfo.InvariantCulture) ?? string.Empty
        };
    }

    private static RecordData ReadRecord(
        DbDataReader reader,
        ObjectDefinition objectDef,
        IReadOnlyList<string>? selectFields = null)
    {
        var selectSet = selectFields is { Count: > 0 }
            ? new HashSet<string>(selectFields, StringComparer.OrdinalIgnoreCase)
            : null;

        var fields = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in objectDef.Fields)
        {
            var exposedName = GetExposedFieldName(field, objectDef.OwnerApp);
            if (selectSet is not null
                && !selectSet.Contains(field.Name)
                && !selectSet.Contains(exposedName))
            {
                continue;
            }

            var colName = field.GetColumnName(objectDef.OwnerApp);
            if (field.Type == FieldType.Money)
            {
                var amountOrd = reader.GetOrdinal($"{colName}_amount");
                var currOrd = reader.GetOrdinal($"{colName}_currency");
                if (reader.IsDBNull(amountOrd) || reader.IsDBNull(currOrd))
                {
                    fields[exposedName] = null;
                }
                else
                {
                    fields[exposedName] = new MoneyValue(
                        reader.GetDecimal(amountOrd),
                        reader.GetString(currOrd));
                }
                continue;
            }

            var ordinal = reader.GetOrdinal(colName);
            if (reader.IsDBNull(ordinal))
            {
                fields[exposedName] = null;
                continue;
            }

            fields[exposedName] = field.Type switch
            {
                FieldType.Date => reader.GetFieldValue<DateOnly>(ordinal).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                FieldType.DateTime => reader.GetFieldValue<DateTimeOffset>(ordinal),
                FieldType.Json => JsonSerializer.Deserialize<object>(reader.GetString(ordinal)),
                _ => reader.GetValue(ordinal)
            };
        }

        return new RecordData
        {
            Id = reader.GetGuid(reader.GetOrdinal("id")),
            TenantId = reader.GetGuid(reader.GetOrdinal("tenant_id")),
            CompanyId = reader.IsDBNull(reader.GetOrdinal("company_id")) ? null : reader.GetGuid(reader.GetOrdinal("company_id")),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
            CreatedBy = reader.GetGuid(reader.GetOrdinal("created_by")),
            UpdatedAt = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("updated_at")),
            UpdatedBy = reader.GetGuid(reader.GetOrdinal("updated_by")),
            RowVersion = reader.GetInt64(reader.GetOrdinal("row_version")),
            Fields = fields
        };
    }

    private static void ValidateUnknownFields(IDictionary<string, object?> inputFields, ObjectDefinition objectDef)
    {
        foreach (var key in inputFields.Keys)
        {
            if (FindField(objectDef, key) is null)
            {
                throw new RecordValidationException($"Field '{key}' is not defined on object '{objectDef.Id}'.", key);
            }
        }
    }

    private static FieldDefinition? FindField(ObjectDefinition objectDef, string key)
    {
        return objectDef.Fields.FirstOrDefault(f =>
            string.Equals(f.Name, key, StringComparison.OrdinalIgnoreCase)
            || string.Equals(GetExposedFieldName(f, objectDef.OwnerApp), key, StringComparison.OrdinalIgnoreCase)
            || string.Equals(f.GetColumnName(objectDef.OwnerApp), key, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryGetFieldInput(
        IDictionary<string, object?> input,
        FieldDefinition field,
        string objectOwnerApp,
        out object? value)
    {
        foreach (var kvp in input)
        {
            if (string.Equals(kvp.Key, field.Name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(kvp.Key, GetExposedFieldName(field, objectOwnerApp), StringComparison.OrdinalIgnoreCase)
                || string.Equals(kvp.Key, field.GetColumnName(objectOwnerApp), StringComparison.OrdinalIgnoreCase))
            {
                value = kvp.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static string GetExposedFieldName(FieldDefinition field, string objectOwnerApp)
    {
        if (string.IsNullOrWhiteSpace(field.OwnerApp)
            || string.Equals(field.OwnerApp, objectOwnerApp, StringComparison.OrdinalIgnoreCase))
        {
            return field.Name;
        }

        return $"{field.OwnerApp}.{field.Name}";
    }

    private async Task<T> ExecuteWithConnectionAsync<T>(
        Func<DbConnection, DbTransaction?, Task<T>> action,
        CancellationToken cancellationToken,
        string? correlationId = null)
    {
        var enlisted = activeTxRegistry?.TryGet(correlationId, tenantContext.TenantId);
        if (enlisted is not null)
        {
            await enlisted.Gate.WaitAsync(cancellationToken);
            try
            {
                return await action(enlisted.Connection, enlisted.Transaction);
            }
            finally
            {
                enlisted.Gate.Release();
            }
        }

        var connection = dbContext.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
        {
            await connection.OpenAsync(cancellationToken);
        }

        DbTransaction? localTx = null;
        try
        {
            var currentTx = dbContext.Database.CurrentTransaction?.GetDbTransaction();
            if (currentTx is null && !string.IsNullOrWhiteSpace(correlationId))
            {
                localTx = await connection.BeginTransactionAsync(cancellationToken);
                currentTx = localTx;
            }

            await using (var setCmd = connection.CreateCommand())
            {
                setCmd.Transaction = currentTx;
                setCmd.CommandText = "/* erp:set_tenant_context */ SELECT set_config('app.tenant_id', @tenantId, false), set_config('app.user_id', @userId, false);";
                AddParameter(setCmd, "tenantId", tenantContext.TenantId?.ToString("D") ?? string.Empty);
                AddParameter(setCmd, "userId", tenantContext.UserId?.ToString("D") ?? string.Empty);
                await setCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            IDisposable? registration = null;
            if (!string.IsNullOrWhiteSpace(correlationId)
                && currentTx is not null
                && tenantContext.TenantId.HasValue
                && activeTxRegistry is not null)
            {
                registration = activeTxRegistry.Register(
                    correlationId,
                    tenantContext.TenantId.Value,
                    connection,
                    currentTx);
            }

            try
            {
                var result = await action(connection, currentTx);
                if (localTx is not null)
                {
                    await localTx.CommitAsync(cancellationToken);
                }
                return result;
            }
            catch (Exception ex)
            {
                if (localTx is not null)
                {
                    await localTx.RollbackAsync(cancellationToken);
                }

                // The database trigger is the guarantee; the caller gets a readable error.
                if (ex is Npgsql.PostgresException { SqlState: "23000" } pg
                    && pg.MessageText.StartsWith("immutable record", StringComparison.Ordinal))
                {
                    throw new RecordValidationException(pg.MessageText, null);
                }

                throw;
            }
            finally
            {
                registration?.Dispose();
            }
        }
        finally
        {
            if (localTx is not null)
            {
                await localTx.DisposeAsync();
            }

            if (shouldClose)
            {
                await connection.CloseAsync();
            }
        }
    }

    private Task PublishRecordEventAsync(
        DbConnection conn,
        DbTransaction? tx,
        string eventName,
        string objectId,
        Guid recordId,
        IDictionary<string, object?>? fields,
        Guid? companyId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        // Written in the same transaction as the data change (transactional outbox, ADR-0009).
        return eventPublisher is null
            ? Task.CompletedTask
            : eventPublisher.PublishAsync(
                conn,
                tx,
                eventName,
                recordId.ToString("D"),
                BuildRecordEventPayload(objectId, recordId, fields),
                companyId,
                correlationId,
                cancellationToken);
    }

    private static Dictionary<string, object?> BuildRecordEventPayload(
        string objectId,
        Guid recordId,
        IDictionary<string, object?>? fields)
    {
        var shortName = objectId.Contains('.')
            ? objectId[(objectId.LastIndexOf('.') + 1)..]
            : objectId;

        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = recordId,
            ["recordId"] = recordId,
            [$"{shortName}Id"] = recordId
        };

        if (fields is not null)
        {
            payload["fields"] = fields;
        }

        return payload;
    }

    private static async Task<string> AllocateNumberAsync(
        DbConnection conn,
        DbTransaction tx,
        ObjectDefinition objectDef,
        FieldDefinition field,
        Guid? companyId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var series = field.NumberSeries!;
        var year = now.UtcDateTime.Year;
        await using var cmd = CreateCommand(conn, tx, "SELECT core.next_number(@company, @object, @field, @period);");
        AddParameter(cmd, "company", (object?)companyId ?? DBNull.Value);
        AddParameter(cmd, "object", objectDef.Id);
        AddParameter(cmd, "field", field.Name);
        AddParameter(cmd, "period", series.PerYear ? year : 0);
        var number = Convert.ToInt64(await cmd.ExecuteScalarAsync(cancellationToken));
        return series.Format(number, year);
    }

    private static Dictionary<string, object?> BuildEffectiveFieldsForCreate(
        IDictionary<string, object?> input,
        ObjectDefinition objectDef)
    {
        var effective = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in objectDef.Fields)
        {
            var exposedName = GetExposedFieldName(field, objectDef.OwnerApp);
            if (TryGetFieldInput(input, field, objectDef.OwnerApp, out var val) && val is not null)
            {
                effective[field.Name] = val;
                effective[exposedName] = val;
            }
            else if (field.DefaultValue is not null)
            {
                effective[field.Name] = field.DefaultValue;
                effective[exposedName] = field.DefaultValue;
            }
        }
        return effective;
    }

    private async Task ValidateConditionalRulesOnUpdateAsync(
        DbConnection conn,
        DbTransaction? tx,
        ObjectDefinition objectDef,
        Guid id,
        IDictionary<string, object?> updateFields,
        CancellationToken cancellationToken)
    {
        var (schema, table) = objectDef.GetSchemaAndTable();
        await using var cmd = CreateCommand(
            conn,
            tx,
            $"""SELECT * FROM "{schema}"."{table}" WHERE id = @id;""");
        AddParameter(cmd, "id", id);

        RecordData? existingRecord = null;
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                existingRecord = ReadRecord(reader, objectDef);
            }
        }

        if (existingRecord is null)
        {
            throw new RecordNotFoundException(objectDef.Id, id);
        }

        if (objectDef.Status is not null)
        {
            var statusFieldName = string.IsNullOrWhiteSpace(objectDef.Status.Field) ? "status" : objectDef.Status.Field;
            if (existingRecord.Fields.TryGetValue(statusFieldName, out var curStatusObj))
            {
                var curStatus = Convert.ToString(curStatusObj, CultureInfo.InvariantCulture) ?? string.Empty;
                var lockingTransition = objectDef.Status.Transitions.Values.FirstOrDefault(
                    t => t.LocksRecord && string.Equals(t.To, curStatus, StringComparison.OrdinalIgnoreCase));
                if (lockingTransition is not null)
                {
                    throw new RecordValidationException(
                        $"Record '{id}' on '{objectDef.Id}' is locked in status '{curStatus}' and cannot be edited.",
                        statusFieldName);
                }
            }
        }

        if (workflowStore is not null
            && await workflowStore.HasRunningInstanceAsync(conn, tx, objectDef.Id, id, cancellationToken))
        {
            var blocked = objectDef.Fields.FirstOrDefault(
                f => !f.EditableInWorkflow && TryGetFieldInput(updateFields, f, objectDef.OwnerApp, out _));
            if (blocked is not null)
            {
                throw new RecordValidationException(
                    $"Record '{id}' on '{objectDef.Id}' is locked by a running workflow; field '{blocked.Name}' cannot be edited.",
                    blocked.Name);
            }
        }

        var mergedFields = new Dictionary<string, object?>(existingRecord.Fields, StringComparer.OrdinalIgnoreCase);
        foreach (var field in objectDef.Fields)
        {
            if (TryGetFieldInput(updateFields, field, objectDef.OwnerApp, out var newVal))
            {
                var exposedName = GetExposedFieldName(field, objectDef.OwnerApp);
                mergedFields[field.Name] = newVal;
                mergedFields[exposedName] = newVal;
            }
        }

        foreach (var field in objectDef.Fields)
        {
            if (field.ReadOnlyWhen is not null
                && TryGetFieldInput(updateFields, field, objectDef.OwnerApp, out _)
                && WhenRuleEvaluator.Evaluate(field.ReadOnlyWhen, existingRecord.Fields))
            {
                throw new RecordValidationException(
                    $"Field '{field.Name}' on '{objectDef.Id}' is read-only under the current conditions.",
                    field.Name);
            }

            if (field.RequiredWhen is not null
                && WhenRuleEvaluator.Evaluate(field.RequiredWhen, mergedFields))
            {
                var exposedName = GetExposedFieldName(field, objectDef.OwnerApp);
                var hasMerged = mergedFields.TryGetValue(exposedName, out var finalVal)
                    || mergedFields.TryGetValue(field.Name, out finalVal);
                if (!hasMerged || WhenRuleEvaluator.IsValueEmpty(finalVal))
                {
                    throw new RecordValidationException(
                        $"Field '{field.Name}' on '{objectDef.Id}' is required under the current conditions.",
                        field.Name);
                }
            }
        }
    }

    private static void ValidateNumericRange(FieldDefinition field, decimal value)
    {
        if (field.MinValue.HasValue && value < field.MinValue.Value)
        {
            throw new RecordValidationException(
                $"Field '{field.Name}' must be at least {field.MinValue.Value.ToString(CultureInfo.InvariantCulture)}.",
                field.Name);
        }

        if (field.MaxValue.HasValue && value > field.MaxValue.Value)
        {
            throw new RecordValidationException(
                $"Field '{field.Name}' must be at most {field.MaxValue.Value.ToString(CultureInfo.InvariantCulture)}.",
                field.Name);
        }
    }

    private static void ValidatePattern(FieldDefinition field, string value)
    {
        if (!string.IsNullOrWhiteSpace(field.Pattern) && !string.IsNullOrEmpty(value))
        {
            if (!Regex.IsMatch(value, field.Pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)))
            {
                throw new RecordValidationException(
                    $"Field '{field.Name}' does not match required pattern '{field.Pattern}'.",
                    field.Name);
            }
        }
    }

    private static DbCommand CreateCommand(DbConnection conn, DbTransaction? tx, string sql)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd;
    }

    private static void AddParameter(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name.StartsWith('@') ? name[1..] : name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
