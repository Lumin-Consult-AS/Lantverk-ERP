using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Erp.Core.Domain.Data;
using Erp.Core.Domain.Tenancy;
using Erp.Core.Infrastructure.Documents;
using Erp.Core.Infrastructure.Security;
using Erp.Core.Infrastructure.Storage;
using Microsoft.Extensions.Options;

namespace Erp.Core.Api.Endpoints;

public sealed record CreateInboxRequest(string Channel, string Name, Guid? CompanyId = null);

public sealed record AttachFileRequest(Guid FileId, string ObjectId, Guid RecordId, string Category);

public sealed record AttachItemRequest(string ObjectId, Guid RecordId, string Category);

public sealed record RejectItemRequest(string? Reason = null);

public sealed record CompleteItemRequest(string? ObjectId = null, Guid? RecordId = null);

public sealed record RenderDocumentRequest(
    string Template,
    JsonElement Data,
    string FileName,
    Guid? CompanyId = null,
    EmailDelivery? Email = null);

/// <summary>Files, attachments, inboxes and inbox items, document generation, and the inbound e-mail webhook.</summary>
public static class DocumentEndpoints
{
    public static void MapDocumentEndpoints(this IEndpointRouteBuilder v1)
    {
        // ---------------------------------------------------------------- files

        v1.MapPost("/files", async (
            HttpRequest request,
            Guid? companyId,
            FileService files,
            PermissionService permissions,
            CancellationToken ct) =>
            await ExecuteAsync(async () =>
            {
                await permissions.RequireAsync("core.file", "create", ct);
                var (name, type, content) = await ReadUploadAsync(request, ct);
                var stored = await files.StoreAsync(name, type, content, companyId, "upload", trusted: false, ct);
                return Results.Json(
                    new { file = stored.File, duplicate = stored.Duplicate },
                    statusCode: stored.Duplicate ? StatusCodes.Status200OK : StatusCodes.Status201Created);
            }));

        v1.MapGet("/files/{id:guid}", async (Guid id, FileService files, PermissionService permissions, CancellationToken ct) =>
            await ExecuteAsync(async () =>
            {
                await permissions.RequireAsync("core.file", "read", ct);
                return await files.GetAsync(id, ct) is { } file ? Results.Ok(file) : Results.NotFound();
            }));

        v1.MapGet("/files/{id:guid}/content", async (Guid id, FileService files, PermissionService permissions, CancellationToken ct) =>
            await ExecuteAsync(async () =>
            {
                await permissions.RequireAsync("core.file", "read", ct);
                var (file, content) = await files.ReadContentAsync(id, ct);
                return Results.File(content, file.ContentType, file.FileName);
            }));

        v1.MapDelete("/files/{id:guid}", async (Guid id, FileService files, PermissionService permissions, CancellationToken ct) =>
            await ExecuteAsync(async () =>
            {
                await permissions.RequireAsync("core.file", "delete", ct);
                await files.DeleteFileAsync(id, ct);
                return Results.NoContent();
            }));

        // ---------------------------------------------------------------- attachments

        v1.MapPost("/attachments", async (AttachFileRequest request, FileService files, PermissionService permissions, CancellationToken ct) =>
            await ExecuteAsync(async () =>
            {
                await permissions.RequireAsync("core.attachment", "create", ct);
                var attachment = await files.AttachAsync(request.FileId, request.ObjectId, request.RecordId, request.Category, ct);
                return Results.Created($"/api/v1/attachments/{attachment.Id}", attachment);
            }));

        v1.MapGet("/attachments", async (
            string objectId,
            Guid recordId,
            FileService files,
            PermissionService permissions,
            CancellationToken ct) =>
            await ExecuteAsync(async () =>
            {
                await permissions.RequireAsync("core.attachment", "read", ct);
                return Results.Ok(await files.ListAttachmentsAsync(objectId, recordId, ct));
            }));

        v1.MapDelete("/attachments/{id:guid}", async (Guid id, FileService files, PermissionService permissions, CancellationToken ct) =>
            await ExecuteAsync(async () =>
            {
                await permissions.RequireAsync("core.attachment", "delete", ct);
                await files.DetachAsync(id, ct);
                return Results.NoContent();
            }));

        // ---------------------------------------------------------------- inboxes and items

        v1.MapGet("/inboxes", async (InboxService inbox, PermissionService permissions, CancellationToken ct) =>
            await ExecuteAsync(async () =>
            {
                await permissions.RequireAsync("core.inbox", "read", ct);
                return Results.Ok(await inbox.ListInboxesAsync(ct));
            }));

        v1.MapPost("/inboxes", async (CreateInboxRequest request, InboxService inbox, PermissionService permissions, CancellationToken ct) =>
            await ExecuteAsync(async () =>
            {
                await permissions.RequireAsync("core.inbox", "create", ct);
                var created = await inbox.CreateInboxAsync(request.Channel, request.Name, request.CompanyId, ct);
                return Results.Created($"/api/v1/inboxes/{created.Id}", created);
            }));

        // Manual or API upload into an inbox: goes through the same scan, dedupe and item creation as e-mail.
        v1.MapPost("/inboxes/{id:guid}/items", async (
            Guid id,
            HttpRequest request,
            InboxService inbox,
            PermissionService permissions,
            ITenantContext tenant,
            CancellationToken ct) =>
            await ExecuteAsync(async () =>
            {
                await permissions.RequireAsync("core.inboxitem", "create", ct);
                var form = await request.ReadFormAsync(ct);
                var attachments = new List<IngestAttachment>();
                foreach (var file in form.Files)
                {
                    await using var stream = file.OpenReadStream();
                    using var buffer = new MemoryStream();
                    await stream.CopyToAsync(buffer, ct);
                    attachments.Add(new IngestAttachment(file.FileName, file.ContentType, buffer.ToArray()));
                }

                var item = await inbox.IngestAsync(
                    id,
                    new IngestItem(
                        "upload", null, tenant.UserId?.ToString("D") ?? string.Empty, null,
                        form["subject"].ToString() is { Length: > 0 } subject ? subject : attachments.FirstOrDefault()?.FileName ?? "Upload",
                        form["text"], DateTimeOffset.UtcNow, attachments),
                    ct);
                return Results.Created($"/api/v1/inbox/items/{item.Id}", item);
            }));

        v1.MapGet("/inbox/items", async (
            string? status,
            Guid? inboxId,
            int? top,
            InboxService inbox,
            PermissionService permissions,
            CancellationToken ct) =>
            await ExecuteAsync(async () =>
            {
                await permissions.RequireAsync("core.inboxitem", "read", ct);
                return Results.Ok(await inbox.ListItemsAsync(status, inboxId, top ?? 50, ct));
            }));

        v1.MapGet("/inbox/items/{id:guid}", async (
            Guid id,
            InboxService inbox,
            FileService files,
            PermissionService permissions,
            CancellationToken ct) =>
            await ExecuteAsync(async () =>
            {
                await permissions.RequireAsync("core.inboxitem", "read", ct);
                if (await inbox.GetItemAsync(id, ct) is not { } item)
                {
                    return Results.NotFound();
                }

                return Results.Ok(new { item, files = await files.GetManyAsync(item.FileIds, ct) });
            }));

        v1.MapPost("/inbox/items/{id:guid}/claim", async (Guid id, InboxService inbox, PermissionService permissions, ITenantContext tenant, CancellationToken ct) =>
            await ExecuteAsync(async () =>
            {
                await permissions.RequireAsync("core.inboxitem", "update", ct);
                return Results.Ok(await inbox.ClaimAsync(id, Claimant(tenant), ct));
            }));

        v1.MapPost("/inbox/items/{id:guid}/release", async (Guid id, InboxService inbox, PermissionService permissions, ITenantContext tenant, CancellationToken ct) =>
            await ExecuteAsync(async () =>
            {
                await permissions.RequireAsync("core.inboxitem", "update", ct);
                return Results.Ok(await inbox.ReleaseAsync(id, Claimant(tenant), ct));
            }));

        v1.MapPost("/inbox/items/{id:guid}/reject", async (
            Guid id,
            RejectItemRequest? request,
            InboxService inbox,
            PermissionService permissions,
            ITenantContext tenant,
            CancellationToken ct) =>
            await ExecuteAsync(async () =>
            {
                await permissions.RequireAsync("core.inboxitem", "update", ct);
                return Results.Ok(await inbox.RejectAsync(id, Claimant(tenant), request?.Reason, ct));
            }));

        v1.MapPost("/inbox/items/{id:guid}/complete", async (
            Guid id,
            CompleteItemRequest? request,
            InboxService inbox,
            PermissionService permissions,
            ITenantContext tenant,
            CancellationToken ct) =>
            await ExecuteAsync(async () =>
            {
                await permissions.RequireAsync("core.inboxitem", "update", ct);
                return Results.Ok(await inbox.CompleteAsync(id, Claimant(tenant), request?.ObjectId, request?.RecordId, ct));
            }));

        // Manual processing: attach the item's files to a record (e.g. a party) and mark the item processed.
        v1.MapPost("/inbox/items/{id:guid}/attach", async (
            Guid id,
            AttachItemRequest request,
            InboxService inbox,
            PermissionService permissions,
            ITenantContext tenant,
            CancellationToken ct) =>
            await ExecuteAsync(async () =>
            {
                await permissions.RequireAsync("core.inboxitem", "update", ct);
                await permissions.RequireAsync("core.attachment", "create", ct);
                return Results.Ok(await inbox.AttachToRecordAsync(id, Claimant(tenant), request.ObjectId, request.RecordId, request.Category, ct));
            }));

        // ---------------------------------------------------------------- generated documents

        v1.MapPost("/documents/render", async (
            RenderDocumentRequest request,
            DocumentGenerator generator,
            PermissionService permissions,
            CancellationToken ct) =>
            await ExecuteAsync(async () =>
            {
                await permissions.RequireAsync("core.file", "create", ct);
                var file = await generator.GeneratePdfAsync(
                    request.Template, request.Data, request.FileName, request.CompanyId, request.Email, ct);
                return Results.Created($"/api/v1/files/{file.Id}", file);
            }));
    }

    /// <summary>
    /// Webhook for Brevo's inbound parsing. Authorised by a secret in the URL (<c>?token=</c>), since Brevo cannot
    /// authenticate as a user. Answers 200 once the message has been handled (also when no inbox matched, so Brevo does
    /// not retry for ever) and 5xx when handling failed, so Brevo redelivers; items are idempotent on the message id.
    /// </summary>
    public static void MapInboundEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapPost("/inbound/brevo", async (
            HttpRequest request,
            string? token,
            IOptions<BrevoOptions> brevo,
            IBrevoAttachmentClient attachmentClient,
            InboundEmailService inbound,
            CancellationToken ct) =>
        {
            var expected = brevo.Value.WebhookToken;
            if (string.IsNullOrWhiteSpace(expected))
            {
                return Results.NotFound();
            }

            if (token is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(expected)))
            {
                return Results.Unauthorized();
            }

            JsonDocument document;
            try
            {
                document = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
            }
            catch (JsonException ex)
            {
                // Not valid UTF-8 JSON (e.g. a client that sent another encoding): a client error, not a server fault.
                return Results.BadRequest(new { error = $"The body is not valid UTF-8 JSON: {ex.Message}" });
            }

            using var _ = document;
            var handled = 0;
            foreach (var email in BrevoInboundAdapter.Parse(document.RootElement, attachmentClient))
            {
                handled += await inbound.ProcessAsync(email, ct);
            }

            return Results.Ok(new { items = handled });
        }).AllowAnonymous().DisableAntiforgery();
    }

    private static string Claimant(ITenantContext tenant) => $"user:{tenant.UserId:D}";

    private static async Task<(string Name, string? Type, byte[] Content)> ReadUploadAsync(HttpRequest request, CancellationToken ct)
    {
        var form = await request.ReadFormAsync(ct);
        var file = form.Files.FirstOrDefault()
            ?? throw new RecordValidationException("Upload a file as multipart form data.", "file");
        await using var stream = file.OpenReadStream();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        return (file.FileName, file.ContentType, buffer.ToArray());
    }

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (PermissionDeniedException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (RecordValidationException ex)
        {
            return Results.BadRequest(new { error = ex.Message, field = ex.FieldName });
        }
        catch (Exception ex) when (ex is RetentionLockedException or InboxItemStateException or ObjectLockedException)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict);
        }
        catch (FileUnavailableException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status423Locked);
        }
    }
}
