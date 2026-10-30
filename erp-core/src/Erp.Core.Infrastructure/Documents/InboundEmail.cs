using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Erp.Core.Domain.Tenancy;
using Erp.Core.Infrastructure.Events;
using Erp.Core.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NpgsqlTypes;

namespace Erp.Core.Infrastructure.Documents;

/// <summary>An attachment of an inbound e-mail. The content is fetched on demand: some providers deliver it separately.</summary>
public sealed record InboundAttachment(
    string FileName,
    string? ContentType,
    long? SizeHint,
    Func<CancellationToken, Task<byte[]>> GetContentAsync);

/// <summary>
/// Provider-neutral inbound e-mail. Each e-mail service gets a small adapter that turns its webhook payload into this
/// contract (ADR-0012), so the inbox does not depend on the provider.
/// </summary>
public sealed record InboundEmail(
    string Provider,
    string? MessageId,
    string From,
    string? FromName,
    IReadOnlyList<string> Recipients,
    string Subject,
    string? Text,
    DateTimeOffset ReceivedAt,
    IReadOnlyList<InboundAttachment> Attachments);

/// <summary>
/// Routes an inbound e-mail to the inbox(es) addressed and creates the inbox items. Recipients that match no inbox are
/// ignored. Safe to call again for the same message (a provider retry): items are idempotent on the message id.
/// </summary>
public sealed class InboundEmailService(
    IServiceScopeFactory scopeFactory,
    DocumentOptions options,
    ILogger<InboundEmailService> logger)
{
    /// <summary>Returns the number of inbox items that exist for the message (created now or by an earlier delivery).</summary>
    public async Task<int> ProcessAsync(InboundEmail email, CancellationToken cancellationToken)
    {
        var inboxes = await ResolveInboxesAsync(email.Recipients, cancellationToken);
        if (inboxes.Count == 0)
        {
            logger.LogInformation("Inbound e-mail from {From} matched no inbox ({Recipients}).", email.From, string.Join(", ", email.Recipients));
            return 0;
        }

        // Fetch the attachments once even when several inboxes are addressed.
        var attachments = new List<IngestAttachment>();
        var skipped = new List<string>();
        foreach (var attachment in email.Attachments)
        {
            if (attachment.SizeHint > options.MaxFileBytes)
            {
                skipped.Add($"{attachment.FileName} (too large)");
                continue;
            }

            var content = await attachment.GetContentAsync(cancellationToken);
            if (content.Length == 0 || content.Length > options.MaxFileBytes)
            {
                skipped.Add($"{attachment.FileName} ({(content.Length == 0 ? "empty" : "too large")})");
                continue;
            }

            attachments.Add(new IngestAttachment(attachment.FileName, attachment.ContentType, content));
        }

        var body = skipped.Count == 0
            ? email.Text
            : $"{email.Text}\n\n[Attachments skipped: {string.Join(", ", skipped)}]";

        var created = 0;
        foreach (var (inboxId, tenantId) in inboxes)
        {
            using var scope = scopeFactory.CreateScope();
            scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId);
            await scope.ServiceProvider.GetRequiredService<InboxService>().IngestAsync(
                inboxId,
                new IngestItem("email", email.MessageId, email.From, email.FromName, email.Subject, body, email.ReceivedAt, attachments),
                cancellationToken);
            created++;
        }

        return created;
    }

    private async Task<List<(Guid InboxId, Guid TenantId)>> ResolveInboxesAsync(
        IReadOnlyList<string> recipients,
        CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CoreDbContext>();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();

        // No tenant yet: the SECURITY DEFINER function maps an address to its inbox.
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        var result = new List<(Guid, Guid)>();
        foreach (var address in recipients.Select(r => r.Trim()).Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await using var cmd = TenantDb.Create(connection, null, "SELECT inbox_id, tenant_id FROM core.resolve_inbox(@address);");
            TenantDb.Add(cmd, "address", NpgsqlDbType.Text, address);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                result.Add((reader.GetGuid(0), reader.GetGuid(1)));
            }
        }

        return result;
    }
}

public sealed class BrevoOptions
{
    public const string SectionName = "Brevo";

    public string ApiBaseUrl { get; set; } = "https://api.brevo.com/v3/";

    /// <summary>API key used to download inbound attachments.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Secret appended to the webhook URL registered at Brevo (<c>?token=</c>). The webhook is refused without it.</summary>
    public string? WebhookToken { get; set; }
}

public interface IBrevoAttachmentClient
{
    Task<byte[]> DownloadAsync(string downloadToken, CancellationToken cancellationToken);
}

/// <summary>Downloads an inbound attachment from Brevo with its download token (<c>GET inbound/attachments/{token}</c>).</summary>
public sealed class BrevoAttachmentClient(HttpClient http) : IBrevoAttachmentClient
{
    public async Task<byte[]> DownloadAsync(string downloadToken, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync($"inbound/attachments/{Uri.EscapeDataString(downloadToken)}", cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }
}

/// <summary>
/// Adapter from Brevo's inbound-parse webhook (<c>{ "items": [ { From, To, Subject, RawTextBody, Attachments: [...] } ] }</c>)
/// to <see cref="InboundEmail"/>. Property names are matched case-insensitively and missing parts are tolerated.
/// </summary>
public static class BrevoInboundAdapter
{
    public static IReadOnlyList<InboundEmail> Parse(JsonElement root, IBrevoAttachmentClient attachmentClient)
    {
        var result = new List<InboundEmail>();
        if (!TryGet(root, "items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in items.EnumerateArray())
        {
            var from = TryGet(item, "From", out var f) ? Address(f) : null;
            var recipients = new List<string>();
            foreach (var field in new[] { "To", "Cc" })
            {
                if (TryGet(item, field, out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    recipients.AddRange(list.EnumerateArray().Select(Address).Where(a => !string.IsNullOrWhiteSpace(a))!);
                }
            }

            var attachments = new List<InboundAttachment>();
            if (TryGet(item, "Attachments", out var atts) && atts.ValueKind == JsonValueKind.Array)
            {
                foreach (var att in atts.EnumerateArray())
                {
                    var token = Str(att, "DownloadToken");
                    if (string.IsNullOrWhiteSpace(token))
                    {
                        continue;
                    }

                    attachments.Add(new InboundAttachment(
                        Str(att, "Name") ?? "attachment",
                        Str(att, "ContentType"),
                        TryGet(att, "ContentLength", out var len) && len.TryGetInt64(out var size) ? size : null,
                        ct => attachmentClient.DownloadAsync(token, ct)));
                }
            }

            result.Add(new InboundEmail(
                "brevo",
                Str(item, "MessageId"),
                from ?? string.Empty,
                TryGet(item, "From", out var fn) ? Str(fn, "Name") : null,
                recipients,
                Str(item, "Subject") ?? string.Empty,
                Str(item, "RawTextBody") ?? Str(item, "ExtractedMarkdownMessage"),
                DateTimeOffset.TryParse(Str(item, "SentAtDate"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var sent)
                    ? sent
                    : DateTimeOffset.UtcNow,
                attachments));
        }

        return result;
    }

    private static string? Address(JsonElement element) => element.ValueKind == JsonValueKind.String ? element.GetString() : Str(element, "Address");

    private static string? Str(JsonElement element, string name) =>
        TryGet(element, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
