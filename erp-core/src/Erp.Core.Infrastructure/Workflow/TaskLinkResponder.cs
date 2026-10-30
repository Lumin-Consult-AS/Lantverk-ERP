using Erp.Core.Domain.Data;
using Erp.Core.Domain.Tenancy;
using Erp.Core.Infrastructure.Events;
using Erp.Core.Infrastructure.Persistence;
using NpgsqlTypes;

namespace Erp.Core.Infrastructure.Workflow;

public enum TaskLinkOutcome
{
    Done,
    InvalidLink,
    NotAllowed,
    NotOpen
}

public sealed record TaskLinkDescription(string Title, string Action, TaskInfo Task);

public sealed record TaskLinkResult(TaskLinkOutcome Outcome, string? Title = null, string? Action = null, string? Message = null);

/// <summary>
/// Executes an approve/reject from a signed e-mail link. The link is verified, then the action runs as the user it
/// was issued to, with that user's current roles, through the same task rules as the worklist.
/// </summary>
public sealed class TaskLinkResponder(
    TaskLinkTokens tokens,
    ITenantContext tenantContext,
    CoreDbContext dbContext,
    WorkflowEngine engine)
{
    public async Task<TaskLinkDescription?> DescribeAsync(string? token, CancellationToken cancellationToken)
    {
        var payload = tokens.Verify(token);
        if (payload is null || !await ActAsAsync(payload, cancellationToken))
        {
            return null;
        }

        var task = await engine.GetTaskAsync(payload.TaskId, cancellationToken);
        return new TaskLinkDescription(task.Title, payload.Action, task);
    }

    public async Task<TaskLinkResult> RespondAsync(string? token, string? comment, CancellationToken cancellationToken)
    {
        var payload = tokens.Verify(token);
        if (payload is null || !await ActAsAsync(payload, cancellationToken))
        {
            return new TaskLinkResult(TaskLinkOutcome.InvalidLink);
        }

        try
        {
            var task = payload.Action == "approve"
                ? await engine.ApproveAsync(payload.TaskId, comment, cancellationToken)
                : await engine.RejectAsync(payload.TaskId, comment, cancellationToken);
            return new TaskLinkResult(TaskLinkOutcome.Done, task.Title, payload.Action);
        }
        catch (UnauthorizedAccessException)
        {
            return new TaskLinkResult(TaskLinkOutcome.NotAllowed);
        }
        catch (RecordValidationException ex)
        {
            return new TaskLinkResult(TaskLinkOutcome.NotOpen, Message: ex.Message);
        }
        catch (KeyNotFoundException)
        {
            return new TaskLinkResult(TaskLinkOutcome.InvalidLink);
        }
    }

    /// <summary>Sets the tenant context to the link's user (with that user's current roles). False when the user is gone or inactive.</summary>
    private async Task<bool> ActAsAsync(TaskLinkPayload payload, CancellationToken cancellationToken)
    {
        tenantContext.Set(payload.TenantId, payload.UserId);
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        await using var cmd = TenantDb.Create(
            connection, null, """SELECT roles, company_id FROM core."user" WHERE id = @id AND is_active;""");
        TenantDb.Add(cmd, "id", NpgsqlDbType.Uuid, payload.UserId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            tenantContext.Clear();
            return false;
        }

        var roles = reader.GetFieldValue<string[]>(0);
        var companyId = reader.IsDBNull(1) ? (Guid?)null : reader.GetGuid(1);
        tenantContext.Set(payload.TenantId, payload.UserId, companyId, roles);
        return true;
    }
}
