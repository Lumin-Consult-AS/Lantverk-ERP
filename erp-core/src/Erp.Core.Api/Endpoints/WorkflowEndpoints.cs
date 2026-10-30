using System.Net;
using Erp.Core.Domain.Data;
using Erp.Core.Domain.Workflow;
using Erp.Core.Infrastructure.Attention;
using Erp.Core.Infrastructure.Metadata;
using Erp.Core.Infrastructure.Workflow;

namespace Erp.Core.Api.Endpoints;

public sealed record TaskActionRequest(string? Comment = null);

public sealed record DelegateTaskRequest(Guid UserId);

public sealed record SnoozeAttentionRequest(DateTimeOffset? Until = null, int? Hours = null);

/// <summary>Workflow definitions, tasks (worklist), the exception-driven start page and the e-mail approval links.</summary>
public static class WorkflowEndpoints
{
    public static void MapWorkflowEndpoints(this IEndpointRouteBuilder v1)
    {
        v1.MapGet("/workflows", async (WorkflowStore store, CancellationToken ct) =>
            Results.Ok((await store.ListDefinitionsAsync(ct))
                .Select(d => new { d.Definition, d.SourceApp, d.IsCustomized })));

        v1.MapGet("/workflows/{id}", async (string id, WorkflowStore store, CancellationToken ct) =>
            await store.GetDefinitionAsync(id, ct) is { } found
                ? Results.Ok(new { found.Definition, found.SourceApp, found.IsCustomized })
                : Results.NotFound(new { error = $"Workflow '{id}' was not found." }));

        v1.MapPut("/workflows/{id}", async (
            string id,
            WorkflowDefinition definition,
            WorkflowStore store,
            MetadataRegistryService metadata,
            CancellationToken ct) =>
            await ExecuteAsync(async () =>
            {
                definition.Id = id;
                var objectDef = await metadata.GetObjectAsync(definition.Object, ct);
                await store.SaveCustomizedDefinitionAsync(definition, objectDef, ct);
                return Results.NoContent();
            })).AddEndpointFilter(EventEndpoints.RequireTenantAdminAsync);

        v1.MapGet("/workflow-instances", async (
            string objectId,
            Guid recordId,
            WorkflowStore store,
            CancellationToken ct) =>
            await store.GetInstanceForRecordAsync(objectId, recordId, ct) is { } instance
                ? Results.Ok(instance)
                : Results.NotFound());

        v1.MapGet("/tasks", async (
            WorkflowEngine engine,
            string? status,
            bool? mine,
            int? top,
            CancellationToken ct) =>
            Results.Ok(await engine.ListTasksAsync(status, mine ?? true, top ?? 50, ct)));

        v1.MapGet("/tasks/{id:guid}", async (Guid id, WorkflowEngine engine, CancellationToken ct) =>
            await ExecuteAsync(async () => Results.Ok(await engine.GetTaskAsync(id, ct))));

        v1.MapPost("/tasks/{id:guid}/approve", async (Guid id, TaskActionRequest? request, WorkflowEngine engine, CancellationToken ct) =>
            await ExecuteAsync(async () => Results.Ok(await engine.ApproveAsync(id, request?.Comment, ct))));

        v1.MapPost("/tasks/{id:guid}/reject", async (Guid id, TaskActionRequest? request, WorkflowEngine engine, CancellationToken ct) =>
            await ExecuteAsync(async () => Results.Ok(await engine.RejectAsync(id, request?.Comment, ct))));

        v1.MapPost("/tasks/{id:guid}/delegate", async (Guid id, DelegateTaskRequest request, WorkflowEngine engine, CancellationToken ct) =>
            await ExecuteAsync(async () => Results.Ok(await engine.DelegateAsync(id, request.UserId, ct))));

        v1.MapGet("/attention", async (AttentionService attention, CancellationToken ct) =>
            Results.Ok(await attention.GetAsync(ct)));

        v1.MapPost("/attention/{id}/hide", async (string id, AttentionService attention, CancellationToken ct) =>
        {
            await attention.HideAsync(id, ct);
            return Results.NoContent();
        });

        v1.MapPost("/attention/{id}/snooze", async (string id, SnoozeAttentionRequest request, AttentionService attention, CancellationToken ct) =>
        {
            var until = request.Until ?? DateTimeOffset.UtcNow.AddHours(request.Hours ?? 24);
            await attention.SnoozeAsync(id, until, ct);
            return Results.NoContent();
        });

        v1.MapDelete("/attention/{id}/dismissal", async (string id, AttentionService attention, CancellationToken ct) =>
        {
            await attention.RestoreAsync(id, ct);
            return Results.NoContent();
        });
    }

    /// <summary>
    /// Approve/reject from an e-mail. The GET only shows a confirmation form (mail scanners and link previews fetch
    /// links); the action itself runs on POST. Both are authorised by the signed link, not by a login.
    /// </summary>
    public static void MapTaskResponseEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/task-response", async (string? token, TaskLinkResponder responder, CancellationToken ct) =>
        {
            var description = await responder.DescribeAsync(token, ct);
            return description is null
                ? Page(StatusCodes.Status400BadRequest, "Ugyldig lenke", "Lenken er ugyldig eller utløpt.")
                : ConfirmPage(token!, description);
        }).AllowAnonymous();

        api.MapPost("/task-response", async (HttpRequest request, TaskLinkResponder responder, CancellationToken ct) =>
        {
            var form = await request.ReadFormAsync(ct);
            var result = await responder.RespondAsync(form["token"], form["comment"], ct);
            return result.Outcome switch
            {
                TaskLinkOutcome.Done => Page(
                    StatusCodes.Status200OK,
                    result.Action == "approve" ? "Godkjent" : "Avvist",
                    $"Oppgaven «{result.Title}» er {(result.Action == "approve" ? "godkjent" : "avvist")}. Du kan lukke denne siden."),
                TaskLinkOutcome.NotOpen => Page(
                    StatusCodes.Status409Conflict, "Allerede behandlet", "Oppgaven er ikke lenger åpen."),
                TaskLinkOutcome.NotAllowed => Page(
                    StatusCodes.Status403Forbidden, "Ikke tillatt", "Du er ikke lenger mottaker av denne oppgaven."),
                _ => Page(StatusCodes.Status400BadRequest, "Ugyldig lenke", "Lenken er ugyldig eller utløpt.")
            };
        }).AllowAnonymous().DisableAntiforgery();
    }

    private static IResult ConfirmPage(string token, TaskLinkDescription description)
    {
        var approve = description.Action == "approve";
        var body =
            $"""
            <p>{WebUtility.HtmlEncode(description.Title)}</p>
            <form method="post" action="/api/v1/task-response">
              <input type="hidden" name="token" value="{WebUtility.HtmlEncode(token)}">
              <p><label>Kommentar (valgfri)<br><textarea name="comment" rows="3" cols="40"></textarea></label></p>
              <button type="submit">{(approve ? "Godkjenn" : "Avvis")}</button>
            </form>
            """;
        return Page(StatusCodes.Status200OK, approve ? "Godkjenn oppgave" : "Avvis oppgave", body, isHtmlBody: true);
    }

    private static IResult Page(int statusCode, string title, string content, bool isHtmlBody = false)
    {
        var body = isHtmlBody ? content : $"<p>{WebUtility.HtmlEncode(content)}</p>";
        var html =
            $"""
            <!doctype html>
            <html lang="nb"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{WebUtility.HtmlEncode(title)}</title></head>
            <body style="font-family:sans-serif;max-width:32rem;margin:2rem auto;padding:0 1rem">
            <h1>{WebUtility.HtmlEncode(title)}</h1>
            {body}
            </body></html>
            """;
        return Results.Content(html, "text/html; charset=utf-8", statusCode: statusCode);
    }

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (RecordValidationException ex)
        {
            return Results.BadRequest(new { error = ex.Message, field = ex.FieldName });
        }
    }
}
