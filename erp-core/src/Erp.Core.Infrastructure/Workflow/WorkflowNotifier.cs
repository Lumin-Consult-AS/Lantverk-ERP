using System.Data.Common;
using Erp.Core.Domain.Tenancy;
using Erp.Core.Infrastructure.Events;
using Erp.Core.Infrastructure.Notifications;
using Erp.Core.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using NpgsqlTypes;

namespace Erp.Core.Infrastructure.Workflow;

/// <summary>
/// E-mails the assignees of newly opened tasks with signed approve/reject links. Sending is best effort and happens
/// after the workflow transaction has committed; a failed e-mail never affects the workflow (the task still shows in
/// the worklist). Tasks assigned to a role are sent to every active user holding that role.
/// </summary>
public sealed class WorkflowNotifier(
    CoreDbContext dbContext,
    ITenantContext tenantContext,
    IEmailSender emailSender,
    TaskLinkTokens tokens,
    EmailOptions options,
    ILogger<WorkflowNotifier> logger)
{
    public async Task NotifyTasksAsync(IEnumerable<Guid> taskIds, CancellationToken cancellationToken)
    {
        var tenantId = tenantContext.TenantId;
        if (tenantId is null)
        {
            return;
        }

        foreach (var taskId in taskIds)
        {
            try
            {
                var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
                await NotifyAsync(connection, tenantId.Value, taskId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not notify assignees of task {TaskId}.", taskId);
            }
        }
    }

    private async Task NotifyAsync(DbConnection connection, Guid tenantId, Guid taskId, CancellationToken cancellationToken)
    {
        string title;
        Guid? assigneeUser;
        string? assigneeRole;
        await using (var cmd = TenantDb.Create(
            connection, null, "SELECT title, assignee_user, assignee_role FROM core.task WHERE id = @id AND status = 'open';"))
        {
            TenantDb.Add(cmd, "id", NpgsqlDbType.Uuid, taskId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return;
            }

            title = reader.GetString(0);
            assigneeUser = reader.IsDBNull(1) ? null : reader.GetGuid(1);
            assigneeRole = reader.IsDBNull(2) ? null : reader.GetString(2);
        }

        var recipients = new List<(Guid Id, string Email)>();
        await using (var cmd = TenantDb.Create(
            connection,
            null,
            """
            SELECT id, email FROM core."user"
             WHERE is_active AND email <> ''
               AND ((@user::uuid IS NOT NULL AND id = @user) OR (@role::text IS NOT NULL AND @role = ANY (roles)));
            """))
        {
            TenantDb.Add(cmd, "user", NpgsqlDbType.Uuid, assigneeUser);
            TenantDb.Add(cmd, "role", NpgsqlDbType.Text, assigneeRole);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                recipients.Add((reader.GetGuid(0), reader.GetString(1)));
            }
        }

        var baseUrl = options.PublicBaseUrl.TrimEnd('/');
        foreach (var (userId, email) in recipients)
        {
            string Link(string action) =>
                $"{baseUrl}/api/v1/task-response?token={Uri.EscapeDataString(tokens.Create(tenantId, taskId, userId, action))}";

            await emailSender.SendAsync(
                new EmailMessage(
                    email,
                    $"Til godkjenning: {title}",
                    $"""
                    Du har en oppgave til godkjenning:

                    {title}

                    Godkjenn: {Link("approve")}
                    Avvis:    {Link("reject")}

                    Lenkene er personlige og gyldige i {options.LinkLifetime.TotalDays:0} dager.
                    """),
                cancellationToken);
        }

        await using var mark = TenantDb.Create(connection, null, "UPDATE core.task SET notified_at = now() WHERE id = @id;");
        TenantDb.Add(mark, "id", NpgsqlDbType.Uuid, taskId);
        await mark.ExecuteNonQueryAsync(cancellationToken);
    }
}
