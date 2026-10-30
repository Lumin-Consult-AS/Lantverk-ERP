using System.Data.Common;
using System.Text.Json;
using Erp.Core.Domain.Data;
using Erp.Core.Domain.Tenancy;
using Erp.Core.Domain.Workflow;
using Erp.Core.Infrastructure.Data;
using Erp.Core.Infrastructure.Events;
using Erp.Core.Infrastructure.Persistence;
using NpgsqlTypes;

namespace Erp.Core.Infrastructure.Workflow;

/// <summary>
/// Moves a running workflow forward: approve / reject / delegate / escalate a task. Every action runs in one
/// transaction that updates the task, opens the next step, performs the record's status transition when the flow
/// ends, and writes the <c>core.workflow.*</c> events to the outbox. Task rows carry the audit trail (who, when,
/// comment) through the standard <c>core.task_audit</c> trigger.
/// </summary>
public sealed class WorkflowEngine(
    CoreDbContext dbContext,
    ITenantContext tenantContext,
    WorkflowStore store,
    RecordDataService recordData,
    EventPublisher eventPublisher,
    WorkflowNotifier notifier)
{
    public async Task<IReadOnlyList<TaskInfo>> ListTasksAsync(
        string? status,
        bool onlyMine,
        int top,
        CancellationToken cancellationToken)
    {
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        await using var cmd = TenantDb.Create(
            connection,
            null,
            $"""
            SELECT {WorkflowStore.TaskColumns} FROM core.task
             WHERE (@status::text IS NULL OR status = @status)
               AND (NOT @mine OR assignee_user = @me OR assignee_role = ANY (@roles))
             ORDER BY created_at DESC
             LIMIT @top;
            """);
        TenantDb.Add(cmd, "status", NpgsqlDbType.Text, string.IsNullOrWhiteSpace(status) ? null : status);
        TenantDb.Add(cmd, "mine", NpgsqlDbType.Boolean, onlyMine);
        TenantDb.Add(cmd, "me", NpgsqlDbType.Uuid, CurrentUser());
        TenantDb.Add(cmd, "roles", NpgsqlDbType.Array | NpgsqlDbType.Text, tenantContext.Roles.ToArray());
        TenantDb.Add(cmd, "top", NpgsqlDbType.Integer, Math.Clamp(top, 1, 200));

        var result = new List<TaskInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(WorkflowStore.ReadTask(reader));
        }

        return result;
    }

    public async Task<int> CountMyOpenTasksAsync(CancellationToken cancellationToken)
    {
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        await using var cmd = TenantDb.Create(
            connection,
            null,
            "SELECT count(*) FROM core.task WHERE status = 'open' AND (assignee_user = @me OR assignee_role = ANY (@roles));");
        TenantDb.Add(cmd, "me", NpgsqlDbType.Uuid, CurrentUser());
        TenantDb.Add(cmd, "roles", NpgsqlDbType.Array | NpgsqlDbType.Text, tenantContext.Roles.ToArray());
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<TaskInfo> GetTaskAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        return await LoadTaskAsync(connection, null, taskId, forUpdate: false, cancellationToken)
            ?? throw new KeyNotFoundException($"Task '{taskId}' was not found.");
    }

    public Task<TaskInfo> ApproveAsync(Guid taskId, string? comment, CancellationToken cancellationToken) =>
        CompleteAsync(taskId, approve: true, comment, cancellationToken);

    public Task<TaskInfo> RejectAsync(Guid taskId, string? comment, CancellationToken cancellationToken) =>
        CompleteAsync(taskId, approve: false, comment, cancellationToken);

    private async Task<TaskInfo> CompleteAsync(Guid taskId, bool approve, string? comment, CancellationToken cancellationToken)
    {
        var correlationId = Guid.NewGuid().ToString("D");
        var userId = CurrentUser();
        var opened = new List<Guid>();

        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        var task = await RequireActionableTaskAsync(connection, tx, taskId, cancellationToken);
        var instance = await LoadInstanceAsync(connection, tx, task.WorkflowInstanceId, cancellationToken);
        var companyId = instance.CompanyId;

        await using (var cmd = TenantDb.Create(
            connection,
            tx,
            """
            UPDATE core.task
               SET status = @status, completed_by = @user, completed_at = now(), comment = @comment,
                   updated_at = now(), updated_by = @user, row_version = row_version + 1
             WHERE id = @id;
            """))
        {
            TenantDb.Add(cmd, "id", NpgsqlDbType.Uuid, taskId);
            TenantDb.Add(cmd, "status", NpgsqlDbType.Varchar, approve ? "approved" : "rejected");
            TenantDb.Add(cmd, "user", NpgsqlDbType.Uuid, userId);
            TenantDb.Add(cmd, "comment", NpgsqlDbType.Text, string.IsNullOrWhiteSpace(comment) ? null : comment.Trim());
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await eventPublisher.PublishAsync(
            connection,
            tx,
            "core.workflow.stepcompleted@1",
            instance.RecordId.ToString("D"),
            new
            {
                instanceId = instance.Id,
                workflowId = instance.Definition.Id,
                objectId = instance.ObjectId,
                recordId = instance.RecordId,
                taskId,
                step = task.StepIndex,
                name = task.Title,
                outcome = approve ? "approved" : "rejected",
                completedBy = userId,
                comment
            },
            companyId,
            correlationId,
            cancellationToken);

        if (!approve)
        {
            await using (var cancel = TenantDb.Create(
                connection,
                tx,
                """
                UPDATE core.task
                   SET status = 'cancelled', updated_at = now(), updated_by = @user, row_version = row_version + 1
                 WHERE workflow_instance_id = @instance AND id <> @id AND status IN ('open', 'waiting');
                """))
            {
                TenantDb.Add(cancel, "instance", NpgsqlDbType.Uuid, instance.Id);
                TenantDb.Add(cancel, "id", NpgsqlDbType.Uuid, taskId);
                TenantDb.Add(cancel, "user", NpgsqlDbType.Uuid, userId);
                await cancel.ExecuteNonQueryAsync(cancellationToken);
            }

            await FinishAsync(connection, tx, instance, "rejected", instance.Definition.OnReject, correlationId, cancellationToken);
        }
        else
        {
            var (stillOpen, waiting) = await CountPendingAsync(connection, tx, instance.Id, cancellationToken);
            if (stillOpen == 0 && waiting > 0)
            {
                opened.Add(await OpenNextStepAsync(connection, tx, instance, userId, cancellationToken));
            }
            else if (stillOpen == 0)
            {
                await FinishAsync(connection, tx, instance, "approved", instance.Definition.OnComplete, correlationId, cancellationToken);
            }
        }

        var updated = await LoadTaskAsync(connection, tx, taskId, forUpdate: false, cancellationToken);
        await tx.CommitAsync(cancellationToken);

        eventPublisher.NotifyCommitted();
        await notifier.NotifyTasksAsync(opened, cancellationToken);
        return updated!;
    }

    /// <summary>Hands an open task to another user. Only allowed when the workflow definition has <c>delegation: allowed</c>.</summary>
    public async Task<TaskInfo> DelegateAsync(Guid taskId, Guid toUserId, CancellationToken cancellationToken)
    {
        var userId = CurrentUser();
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        var task = await RequireActionableTaskAsync(connection, tx, taskId, cancellationToken);
        var instance = await LoadInstanceAsync(connection, tx, task.WorkflowInstanceId, cancellationToken);
        if (!instance.Definition.AllowsDelegation)
        {
            throw new RecordValidationException($"Workflow '{instance.Definition.Id}' does not allow delegation.");
        }

        await using (var exists = TenantDb.Create(
            connection, tx, """SELECT 1 FROM core."user" WHERE id = @id AND is_active;"""))
        {
            TenantDb.Add(exists, "id", NpgsqlDbType.Uuid, toUserId);
            if (await exists.ExecuteScalarAsync(cancellationToken) is null)
            {
                throw new RecordValidationException($"User '{toUserId}' does not exist or is inactive.", "userId");
            }
        }

        await using (var cmd = TenantDb.Create(
            connection,
            tx,
            """
            UPDATE core.task
               SET assignee_user = @to, assignee_role = NULL, delegated_from = @me, notified_at = NULL,
                   updated_at = now(), updated_by = @me, row_version = row_version + 1
             WHERE id = @id;
            """))
        {
            TenantDb.Add(cmd, "id", NpgsqlDbType.Uuid, taskId);
            TenantDb.Add(cmd, "to", NpgsqlDbType.Uuid, toUserId);
            TenantDb.Add(cmd, "me", NpgsqlDbType.Uuid, userId);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        var updated = await LoadTaskAsync(connection, tx, taskId, forUpdate: false, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        await notifier.NotifyTasksAsync([taskId], cancellationToken);
        return updated!;
    }

    /// <summary>
    /// Called for a task whose deadline has passed: reassigns it to the workflow's escalation target. Runs as the
    /// system, so it does not require the actor to be the assignee.
    /// </summary>
    public async Task EscalateAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        var task = await LoadTaskAsync(connection, tx, taskId, forUpdate: true, cancellationToken);
        if (task is null || task.Status != "open")
        {
            return;
        }

        var instance = await LoadInstanceAsync(connection, tx, task.WorkflowInstanceId, cancellationToken);
        var target = instance.Definition.Deadline?.EscalateTo;
        if (target is null)
        {
            await tx.CommitAsync(cancellationToken);
            return;
        }

        await using (var cmd = TenantDb.Create(
            connection,
            tx,
            """
            UPDATE core.task
               SET assignee_user = @user, assignee_role = @role, notified_at = NULL,
                   updated_at = now(), row_version = row_version + 1
             WHERE id = @id;
            """))
        {
            TenantDb.Add(cmd, "id", NpgsqlDbType.Uuid, taskId);
            TenantDb.Add(cmd, "user", NpgsqlDbType.Uuid, target.User);
            TenantDb.Add(cmd, "role", NpgsqlDbType.Varchar, string.IsNullOrWhiteSpace(target.Role) ? null : target.Role);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        await notifier.NotifyTasksAsync([taskId], cancellationToken);
    }

    // ------------------------------------------------------------------ helpers

    private async Task FinishAsync(
        DbConnection connection,
        DbTransaction tx,
        InstanceRow instance,
        string outcome,
        string? transitionName,
        string correlationId,
        CancellationToken cancellationToken)
    {
        await using (var cmd = TenantDb.Create(
            connection,
            tx,
            """
            UPDATE core.workflow_instance
               SET status = @status, completed_at = now(), updated_at = now(), updated_by = @user, row_version = row_version + 1
             WHERE id = @id;
            """))
        {
            TenantDb.Add(cmd, "id", NpgsqlDbType.Uuid, instance.Id);
            TenantDb.Add(cmd, "status", NpgsqlDbType.Varchar, outcome == "approved" ? "completed" : "rejected");
            TenantDb.Add(cmd, "user", NpgsqlDbType.Uuid, CurrentUser());
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // The instance is no longer running, so the record's lock is released before the outcome transition runs.
        if (!string.IsNullOrWhiteSpace(transitionName))
        {
            await recordData.ApplyWorkflowOutcomeAsync(
                connection, tx, instance.ObjectId, instance.RecordId, transitionName, correlationId, cancellationToken);
        }

        await store.PublishCompletedAsync(
            connection, tx, instance.Id, instance.Definition.Id, instance.ObjectId, instance.RecordId, outcome,
            instance.CompanyId, correlationId, cancellationToken);
    }

    private async Task<Guid> OpenNextStepAsync(
        DbConnection connection,
        DbTransaction tx,
        InstanceRow instance,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var days = instance.Definition.Deadline is { Days: > 0 } d ? d.Days : (int?)null;
        await using var cmd = TenantDb.Create(
            connection,
            tx,
            """
            UPDATE core.task
               SET status = 'open', due_at = @due, updated_at = now(), updated_by = @user, row_version = row_version + 1
             WHERE id = (SELECT id FROM core.task
                          WHERE workflow_instance_id = @instance AND status = 'waiting'
                          ORDER BY step_index LIMIT 1)
            RETURNING id;
            """);
        TenantDb.Add(cmd, "instance", NpgsqlDbType.Uuid, instance.Id);
        TenantDb.Add(cmd, "user", NpgsqlDbType.Uuid, userId);
        TenantDb.Add(cmd, "due", NpgsqlDbType.TimestampTz, days is null ? null : DateTimeOffset.UtcNow.AddDays(days.Value));
        return (Guid)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<(int Open, int Waiting)> CountPendingAsync(
        DbConnection connection,
        DbTransaction tx,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        await using var cmd = TenantDb.Create(
            connection,
            tx,
            """
            SELECT count(*) FILTER (WHERE status = 'open'), count(*) FILTER (WHERE status = 'waiting')
              FROM core.task WHERE workflow_instance_id = @id;
            """);
        TenantDb.Add(cmd, "id", NpgsqlDbType.Uuid, instanceId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return ((int)reader.GetInt64(0), (int)reader.GetInt64(1));
    }

    private async Task<TaskInfo> RequireActionableTaskAsync(
        DbConnection connection,
        DbTransaction tx,
        Guid taskId,
        CancellationToken cancellationToken)
    {
        var task = await LoadTaskAsync(connection, tx, taskId, forUpdate: true, cancellationToken)
            ?? throw new KeyNotFoundException($"Task '{taskId}' was not found.");

        if (task.Status != "open")
        {
            throw new RecordValidationException($"Task '{taskId}' is {task.Status} and cannot be acted on.");
        }

        var isAssignee = task.AssigneeUser == CurrentUser()
            || (task.AssigneeRole is not null
                && tenantContext.Roles.Contains(task.AssigneeRole, StringComparer.OrdinalIgnoreCase));
        if (!isAssignee)
        {
            throw new UnauthorizedAccessException("Only the task's assignee can act on it.");
        }

        return task;
    }

    private static async Task<TaskInfo?> LoadTaskAsync(
        DbConnection connection,
        DbTransaction? tx,
        Guid taskId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        await using var cmd = TenantDb.Create(
            connection,
            tx,
            $"SELECT {WorkflowStore.TaskColumns} FROM core.task WHERE id = @id{(forUpdate ? " FOR UPDATE" : string.Empty)};");
        TenantDb.Add(cmd, "id", NpgsqlDbType.Uuid, taskId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? WorkflowStore.ReadTask(reader) : null;
    }

    private static async Task<InstanceRow> LoadInstanceAsync(
        DbConnection connection,
        DbTransaction tx,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        await using var cmd = TenantDb.Create(
            connection,
            tx,
            """
            SELECT id, object_id, record_id, company_id, status, definition::text
              FROM core.workflow_instance WHERE id = @id FOR UPDATE;
            """);
        TenantDb.Add(cmd, "id", NpgsqlDbType.Uuid, instanceId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new KeyNotFoundException($"Workflow instance '{instanceId}' was not found.");
        }

        var instance = new InstanceRow(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetGuid(2),
            reader.IsDBNull(3) ? null : reader.GetGuid(3),
            reader.GetString(4),
            JsonSerializer.Deserialize<WorkflowDefinition>(reader.GetString(5), WorkflowStore.JsonOptions)!);

        return instance.Status == "running"
            ? instance
            : throw new RecordValidationException($"Workflow instance '{instanceId}' is {instance.Status}.");
    }

    private Guid CurrentUser() => tenantContext.UserId
        ?? throw new UnauthorizedAccessException("A user is required to act on tasks.");

    private sealed record InstanceRow(
        Guid Id,
        string ObjectId,
        Guid RecordId,
        Guid? CompanyId,
        string Status,
        WorkflowDefinition Definition);
}
