using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using Erp.Core.Domain.Apps;
using Erp.Core.Domain.Data;
using Erp.Core.Domain.Metadata;
using Erp.Core.Domain.Tenancy;
using Erp.Core.Domain.Workflow;
using Erp.Core.Infrastructure.Events;
using Erp.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NpgsqlTypes;

namespace Erp.Core.Infrastructure.Workflow;

public sealed record WorkflowStartResult(
    Guid InstanceId,
    WorkflowDefinition Definition,
    IReadOnlyList<Guid> OpenedTaskIds,
    bool NoStepsApply);

public sealed record WorkflowInstanceInfo(
    Guid Id,
    string WorkflowId,
    string ObjectId,
    Guid RecordId,
    string Transition,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<TaskInfo> Tasks);

public sealed record TaskInfo(
    Guid Id,
    Guid WorkflowInstanceId,
    int StepIndex,
    string Title,
    string ObjectId,
    Guid RecordId,
    string Link,
    Guid? AssigneeUser,
    string? AssigneeRole,
    string Status,
    DateTimeOffset? DueAt,
    bool Escalated,
    Guid? DelegatedFrom,
    Guid? CompletedBy,
    DateTimeOffset? CompletedAt,
    string? Comment,
    DateTimeOffset CreatedAt);

public sealed record StoredWorkflowDefinition(WorkflowDefinition Definition, string? SourceApp, bool IsCustomized);

/// <summary>
/// Persistence for workflow definitions (tenant data), running instances and tasks. Methods that take a
/// connection/transaction join the caller's transaction so state changes, tasks and events commit together.
/// </summary>
public sealed class WorkflowStore(
    CoreDbContext dbContext,
    ITenantContext tenantContext,
    EventPublisher eventPublisher)
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal const string TaskColumns =
        "id, workflow_instance_id, step_index, title, object_id, record_id, link, assignee_user, assignee_role, status, due_at, " +
        "escalated_at IS NOT NULL, delegated_from, completed_by, completed_at, comment, created_at";

    // ---------------------------------------------------------------- definitions

    /// <summary>Copies an app's default workflows into the tenant's data. Tenant-customized definitions are left untouched.</summary>
    public async Task SeedFromManifestAsync(AppManifest manifest, CancellationToken cancellationToken)
    {
        if (manifest.Workflows.Count == 0)
        {
            return;
        }

        var (connection, tx) = await OpenAsync(cancellationToken);
        foreach (var workflow in manifest.Workflows)
        {
            await using var cmd = TenantDb.Create(
                connection,
                tx,
                """
                INSERT INTO core.workflow_definition (id, tenant_id, created_by, updated_by, workflow_id, object_id, definition, source_app)
                VALUES (@id, @tenantId, @userId, @userId, @workflowId, @objectId, @definition::jsonb, @app)
                ON CONFLICT (tenant_id, workflow_id) DO UPDATE
                   SET definition = EXCLUDED.definition, object_id = EXCLUDED.object_id, source_app = EXCLUDED.source_app,
                       updated_at = now(), updated_by = EXCLUDED.updated_by, row_version = core.workflow_definition.row_version + 1
                 WHERE NOT core.workflow_definition.is_customized;
                """);
            TenantDb.Add(cmd, "id", NpgsqlDbType.Uuid, Guid.NewGuid());
            TenantDb.Add(cmd, "tenantId", NpgsqlDbType.Uuid, RequireTenant());
            TenantDb.Add(cmd, "userId", NpgsqlDbType.Uuid, CurrentUser());
            TenantDb.Add(cmd, "workflowId", NpgsqlDbType.Varchar, workflow.Id);
            TenantDb.Add(cmd, "objectId", NpgsqlDbType.Varchar, workflow.Object);
            TenantDb.Add(cmd, "definition", NpgsqlDbType.Text, JsonSerializer.Serialize(workflow, JsonOptions));
            TenantDb.Add(cmd, "app", NpgsqlDbType.Varchar, manifest.Id);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>Removes the uninstalled app's workflow defaults that the tenant has not customized.</summary>
    public async Task RemoveAppDefaultsAsync(string appId, CancellationToken cancellationToken)
    {
        var (connection, tx) = await OpenAsync(cancellationToken);
        await using var cmd = TenantDb.Create(
            connection,
            tx,
            "DELETE FROM core.workflow_definition WHERE source_app = @app AND NOT is_customized;");
        TenantDb.Add(cmd, "app", NpgsqlDbType.Varchar, appId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<StoredWorkflowDefinition>> ListDefinitionsAsync(CancellationToken cancellationToken)
    {
        var (connection, tx) = await OpenAsync(cancellationToken);
        await using var cmd = TenantDb.Create(
            connection, tx, "SELECT definition::text, source_app, is_customized FROM core.workflow_definition ORDER BY workflow_id;");
        var result = new List<StoredWorkflowDefinition>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadDefinition(reader));
        }

        return result;
    }

    public async Task<StoredWorkflowDefinition?> GetDefinitionAsync(
        DbConnection connection,
        DbTransaction? tx,
        string workflowId,
        CancellationToken cancellationToken)
    {
        await using var cmd = TenantDb.Create(
            connection, tx, "SELECT definition::text, source_app, is_customized FROM core.workflow_definition WHERE workflow_id = @id;");
        TenantDb.Add(cmd, "id", NpgsqlDbType.Varchar, workflowId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadDefinition(reader) : null;
    }

    public async Task<StoredWorkflowDefinition?> GetDefinitionAsync(string workflowId, CancellationToken cancellationToken)
    {
        var (connection, tx) = await OpenAsync(cancellationToken);
        return await GetDefinitionAsync(connection, tx, workflowId, cancellationToken);
    }

    /// <summary>Stores the tenant's edit of a workflow; it is then no longer overwritten by app updates.</summary>
    public async Task SaveCustomizedDefinitionAsync(
        WorkflowDefinition definition,
        ObjectDefinition? objectDef,
        CancellationToken cancellationToken)
    {
        var errors = WorkflowDefinitionValidator.Validate(definition, objectDef);
        if (objectDef is null)
        {
            errors.Add($"Workflow '{definition.Id}' refers to unknown object '{definition.Object}'.");
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(string.Join(" ", errors));
        }

        var (connection, tx) = await OpenAsync(cancellationToken);
        await using var cmd = TenantDb.Create(
            connection,
            tx,
            """
            INSERT INTO core.workflow_definition (id, tenant_id, created_by, updated_by, workflow_id, object_id, definition, is_customized)
            VALUES (@id, @tenantId, @userId, @userId, @workflowId, @objectId, @definition::jsonb, true)
            ON CONFLICT (tenant_id, workflow_id) DO UPDATE
               SET definition = EXCLUDED.definition, object_id = EXCLUDED.object_id, is_customized = true,
                   updated_at = now(), updated_by = EXCLUDED.updated_by, row_version = core.workflow_definition.row_version + 1;
            """);
        TenantDb.Add(cmd, "id", NpgsqlDbType.Uuid, Guid.NewGuid());
        TenantDb.Add(cmd, "tenantId", NpgsqlDbType.Uuid, RequireTenant());
        TenantDb.Add(cmd, "userId", NpgsqlDbType.Uuid, CurrentUser());
        TenantDb.Add(cmd, "workflowId", NpgsqlDbType.Varchar, definition.Id);
        TenantDb.Add(cmd, "objectId", NpgsqlDbType.Varchar, definition.Object);
        TenantDb.Add(cmd, "definition", NpgsqlDbType.Text, JsonSerializer.Serialize(definition, JsonOptions));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    // ---------------------------------------------------------------- instances

    public async Task<bool> HasRunningInstanceAsync(
        DbConnection connection,
        DbTransaction? tx,
        string objectId,
        Guid recordId,
        CancellationToken cancellationToken)
    {
        await using var cmd = TenantDb.Create(
            connection,
            tx,
            "SELECT 1 FROM core.workflow_instance WHERE object_id = @objectId AND record_id = @recordId AND status = 'running';");
        TenantDb.Add(cmd, "objectId", NpgsqlDbType.Varchar, objectId);
        TenantDb.Add(cmd, "recordId", NpgsqlDbType.Uuid, recordId);
        return await cmd.ExecuteScalarAsync(cancellationToken) is not null;
    }

    /// <summary>
    /// Starts the workflow for a record that has just entered the transition's target status. Only the steps whose
    /// <c>when</c> rule holds for the record are created. With no applicable step the instance completes at once.
    /// </summary>
    public async Task<WorkflowStartResult> StartAsync(
        DbConnection connection,
        DbTransaction? tx,
        ObjectDefinition objectDef,
        RecordData record,
        string transitionName,
        string workflowId,
        Guid? companyId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var stored = await GetDefinitionAsync(connection, tx, workflowId, cancellationToken)
            ?? throw new RecordValidationException(
                $"Workflow '{workflowId}' used by transition '{transitionName}' on '{objectDef.Id}' is not defined in this tenant.");
        var definition = stored.Definition;

        var steps = definition.Steps
            .Select((step, index) => (step, index))
            .Where(x => x.step.When is null || x.step.When.Matches(record.Fields))
            .ToList();

        var tenantId = RequireTenant();
        var userId = CurrentUser();
        var instanceId = Guid.NewGuid();
        var noSteps = steps.Count == 0;
        var parallel = string.Equals(definition.Mode, WorkflowModes.Parallel, StringComparison.OrdinalIgnoreCase);

        await using (var cmd = TenantDb.Create(
            connection,
            tx,
            """
            INSERT INTO core.workflow_instance
                (id, tenant_id, company_id, created_by, updated_by, workflow_id, definition, object_id, record_id, transition, status, completed_at)
            VALUES
                (@id, @tenantId, @companyId, @userId, @userId, @workflowId, @definition::jsonb, @objectId, @recordId, @transition,
                 @status, CASE WHEN @status = 'completed' THEN now() ELSE NULL END);
            """))
        {
            TenantDb.Add(cmd, "id", NpgsqlDbType.Uuid, instanceId);
            TenantDb.Add(cmd, "tenantId", NpgsqlDbType.Uuid, tenantId);
            TenantDb.Add(cmd, "companyId", NpgsqlDbType.Uuid, companyId);
            TenantDb.Add(cmd, "userId", NpgsqlDbType.Uuid, userId);
            TenantDb.Add(cmd, "workflowId", NpgsqlDbType.Varchar, definition.Id);
            TenantDb.Add(cmd, "definition", NpgsqlDbType.Text, JsonSerializer.Serialize(definition, JsonOptions));
            TenantDb.Add(cmd, "objectId", NpgsqlDbType.Varchar, objectDef.Id);
            TenantDb.Add(cmd, "recordId", NpgsqlDbType.Uuid, record.Id);
            TenantDb.Add(cmd, "transition", NpgsqlDbType.Varchar, transitionName);
            TenantDb.Add(cmd, "status", NpgsqlDbType.Varchar, noSteps ? "completed" : "running");
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        var opened = new List<Guid>();
        var display = DisplayName(objectDef, record);
        for (var i = 0; i < steps.Count; i++)
        {
            var (step, _) = steps[i];
            var taskId = Guid.NewGuid();
            var open = parallel || i == 0;
            await InsertTaskAsync(
                connection, tx, tenantId, userId, companyId, instanceId, i, $"{step.Name}: {display}", objectDef.Id, record.Id,
                taskId, step.Assignee, open, definition.Deadline, cancellationToken);
            if (open)
            {
                opened.Add(taskId);
            }
        }

        await eventPublisher.PublishAsync(
            connection,
            tx,
            "core.workflow.started@1",
            record.Id.ToString("D"),
            new
            {
                instanceId,
                workflowId = definition.Id,
                objectId = objectDef.Id,
                recordId = record.Id,
                transition = transitionName,
                steps = steps.Select(s => s.step.Name).ToArray()
            },
            companyId,
            correlationId,
            cancellationToken);

        if (noSteps)
        {
            await PublishCompletedAsync(connection, tx, instanceId, definition.Id, objectDef.Id, record.Id, "approved", companyId, correlationId, cancellationToken);
        }

        return new WorkflowStartResult(instanceId, definition, opened, noSteps);
    }

    internal async Task InsertTaskAsync(
        DbConnection connection,
        DbTransaction? tx,
        Guid tenantId,
        Guid userId,
        Guid? companyId,
        Guid instanceId,
        int stepIndex,
        string title,
        string objectId,
        Guid recordId,
        Guid taskId,
        WorkflowAssignee assignee,
        bool open,
        WorkflowDeadline? deadline,
        CancellationToken cancellationToken)
    {
        await using var cmd = TenantDb.Create(
            connection,
            tx,
            """
            INSERT INTO core.task
                (id, tenant_id, company_id, created_by, updated_by, workflow_instance_id, step_index, title, object_id, record_id, link,
                 assignee_user, assignee_role, status, due_at)
            VALUES
                (@id, @tenantId, @companyId, @userId, @userId, @instanceId, @step, @title, @objectId, @recordId, @link,
                 @assigneeUser, @assigneeRole, @status, @dueAt);
            """);
        TenantDb.Add(cmd, "id", NpgsqlDbType.Uuid, taskId);
        TenantDb.Add(cmd, "tenantId", NpgsqlDbType.Uuid, tenantId);
        TenantDb.Add(cmd, "companyId", NpgsqlDbType.Uuid, companyId);
        TenantDb.Add(cmd, "userId", NpgsqlDbType.Uuid, userId);
        TenantDb.Add(cmd, "instanceId", NpgsqlDbType.Uuid, instanceId);
        TenantDb.Add(cmd, "step", NpgsqlDbType.Integer, stepIndex);
        TenantDb.Add(cmd, "title", NpgsqlDbType.Varchar, title.Length > 300 ? title[..300] : title);
        TenantDb.Add(cmd, "objectId", NpgsqlDbType.Varchar, objectId);
        TenantDb.Add(cmd, "recordId", NpgsqlDbType.Uuid, recordId);
        TenantDb.Add(cmd, "link", NpgsqlDbType.Varchar, $"/tasks/{taskId:D}");
        TenantDb.Add(cmd, "assigneeUser", NpgsqlDbType.Uuid, assignee.User);
        TenantDb.Add(cmd, "assigneeRole", NpgsqlDbType.Varchar, string.IsNullOrWhiteSpace(assignee.Role) ? null : assignee.Role);
        TenantDb.Add(cmd, "status", NpgsqlDbType.Varchar, open ? "open" : "waiting");
        TenantDb.Add(
            cmd, "dueAt", NpgsqlDbType.TimestampTz,
            open && deadline is { Days: > 0 } ? DateTimeOffset.UtcNow.AddDays(deadline.Days) : null);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    internal Task PublishCompletedAsync(
        DbConnection connection,
        DbTransaction? tx,
        Guid instanceId,
        string workflowId,
        string objectId,
        Guid recordId,
        string outcome,
        Guid? companyId,
        string correlationId,
        CancellationToken cancellationToken) =>
        eventPublisher.PublishAsync(
            connection,
            tx,
            "core.workflow.completed@1",
            recordId.ToString("D"),
            new { instanceId, workflowId, objectId, recordId, outcome },
            companyId,
            correlationId,
            cancellationToken);

    public async Task<WorkflowInstanceInfo?> GetInstanceForRecordAsync(
        string objectId,
        Guid recordId,
        CancellationToken cancellationToken)
    {
        var (connection, tx) = await OpenAsync(cancellationToken);

        Guid? instanceId = null;
        await using (var cmd = TenantDb.Create(
            connection,
            tx,
            "SELECT id FROM core.workflow_instance WHERE object_id = @o AND record_id = @r ORDER BY created_at DESC LIMIT 1;"))
        {
            TenantDb.Add(cmd, "o", NpgsqlDbType.Varchar, objectId);
            TenantDb.Add(cmd, "r", NpgsqlDbType.Uuid, recordId);
            instanceId = await cmd.ExecuteScalarAsync(cancellationToken) as Guid?;
        }

        return instanceId is null ? null : await GetInstanceAsync(connection, tx, instanceId.Value, cancellationToken);
    }

    public async Task<WorkflowInstanceInfo?> GetInstanceAsync(
        DbConnection connection,
        DbTransaction? tx,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        WorkflowInstanceInfo? instance = null;
        await using (var cmd = TenantDb.Create(
            connection,
            tx,
            """
            SELECT id, workflow_id, object_id, record_id, transition, status, created_at, completed_at
              FROM core.workflow_instance WHERE id = @id;
            """))
        {
            TenantDb.Add(cmd, "id", NpgsqlDbType.Uuid, instanceId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                instance = new WorkflowInstanceInfo(
                    reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetGuid(3), reader.GetString(4),
                    reader.GetString(5), reader.GetFieldValue<DateTimeOffset>(6),
                    reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7), []);
            }
        }

        if (instance is null)
        {
            return null;
        }

        var tasks = new List<TaskInfo>();
        await using (var cmd = TenantDb.Create(
            connection,
            tx,
            $"SELECT {TaskColumns} FROM core.task WHERE workflow_instance_id = @id ORDER BY step_index, created_at;"))
        {
            TenantDb.Add(cmd, "id", NpgsqlDbType.Uuid, instanceId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                tasks.Add(ReadTask(reader));
            }
        }

        return instance with { Tasks = tasks };
    }

    internal static TaskInfo ReadTask(DbDataReader r) => new(
        r.GetGuid(0), r.GetGuid(1), r.GetInt32(2), r.GetString(3), r.GetString(4), r.GetGuid(5), r.GetString(6),
        r.IsDBNull(7) ? null : r.GetGuid(7),
        r.IsDBNull(8) ? null : r.GetString(8),
        r.GetString(9),
        r.IsDBNull(10) ? null : r.GetFieldValue<DateTimeOffset>(10),
        r.GetBoolean(11),
        r.IsDBNull(12) ? null : r.GetGuid(12),
        r.IsDBNull(13) ? null : r.GetGuid(13),
        r.IsDBNull(14) ? null : r.GetFieldValue<DateTimeOffset>(14),
        r.IsDBNull(15) ? null : r.GetString(15),
        r.GetFieldValue<DateTimeOffset>(16));

    internal static string DisplayName(ObjectDefinition objectDef, RecordData record)
    {
        foreach (var candidate in new[] { "title", "name", "description", "number" })
        {
            if (record.Fields.TryGetValue(candidate, out var value) && value is string { Length: > 0 } text)
            {
                return text;
            }
        }

        return $"{objectDef.Label} {record.Id.ToString("D")[..8]}";
    }

    private static StoredWorkflowDefinition ReadDefinition(DbDataReader reader) => new(
        JsonSerializer.Deserialize<WorkflowDefinition>(reader.GetString(0), JsonOptions)!,
        reader.IsDBNull(1) ? null : reader.GetString(1),
        reader.GetBoolean(2));

    private async Task<(DbConnection Connection, DbTransaction? Tx)> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        return (connection, dbContext.Database.CurrentTransaction?.GetDbTransaction());
    }

    private Guid RequireTenant() => tenantContext.TenantId
        ?? throw new InvalidOperationException("Workflow operations require an active tenant context.");

    private Guid CurrentUser() => tenantContext.UserId ?? RequireTenant();
}
