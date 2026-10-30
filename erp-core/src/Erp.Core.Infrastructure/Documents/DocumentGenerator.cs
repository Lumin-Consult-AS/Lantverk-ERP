using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Erp.Core.Domain.Data;
using Erp.Core.Infrastructure.Notifications;

namespace Erp.Core.Infrastructure.Documents;

public sealed class PdfRendererOptions
{
    public const string SectionName = "PdfRenderer";

    /// <summary>Base URL of the PDF renderer container (Playwright/Chromium headless).</summary>
    public string? BaseUrl { get; set; }

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);
}

public interface IPdfRenderer
{
    Task<byte[]> RenderAsync(string html, CancellationToken cancellationToken);
}

/// <summary>Sends the HTML to the renderer container (<c>POST /render</c>) and returns the PDF it produces.</summary>
public sealed class HttpPdfRenderer(HttpClient http) : IPdfRenderer
{
    public async Task<byte[]> RenderAsync(string html, CancellationToken cancellationToken)
    {
        using var content = new StringContent(html, System.Text.Encoding.UTF8, "text/html");
        using var response = await http.PostAsync("render", content, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }
}

/// <summary>Registered when no renderer is configured.</summary>
public sealed class UnconfiguredPdfRenderer : IPdfRenderer
{
    public Task<byte[]> RenderAsync(string html, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("PDF generation is not configured. Set PdfRenderer:BaseUrl.");
}

/// <summary>
/// Fills an HTML template: <c>{{key}}</c> and <c>{{path.to.key}}</c> are replaced by the HTML-encoded value from the
/// data. Unknown keys become empty. Values are always encoded, so data cannot inject markup into the document.
/// </summary>
public static partial class HtmlTemplate
{
    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_.\-]+)\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    public static string Render(string template, JsonElement data) =>
        Placeholder().Replace(template, match => WebUtility.HtmlEncode(Lookup(data, match.Groups[1].Value)));

    private static string Lookup(JsonElement data, string path)
    {
        var current = data;
        foreach (var part in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object || !TryGetIgnoreCase(current, part, out current))
            {
                return string.Empty;
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString() ?? string.Empty,
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            JsonValueKind.Object or JsonValueKind.Array => string.Empty,
            _ => current.ToString()
        };
    }

    private static bool TryGetIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}

public sealed record EmailDelivery(string To, string Subject, string? Body);

/// <summary>
/// Generates a PDF from an HTML template and data, stores it as a file (the platform made it, so it is not scanned),
/// and can send it by e-mail as an attachment.
/// </summary>
public sealed class DocumentGenerator(
    IPdfRenderer renderer,
    FileService files,
    IEmailSender emailSender)
{
    public async Task<StoredFile> GeneratePdfAsync(
        string htmlTemplate,
        JsonElement data,
        string fileName,
        Guid? companyId,
        EmailDelivery? email,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(htmlTemplate))
        {
            throw new RecordValidationException("A template is required.", "template");
        }

        if (email is not null && string.IsNullOrWhiteSpace(email.To))
        {
            throw new RecordValidationException("The e-mail recipient is required.", "email.to");
        }

        var html = HtmlTemplate.Render(htmlTemplate, data);
        var pdf = await renderer.RenderAsync(html, cancellationToken);
        var name = fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? fileName : fileName + ".pdf";
        var stored = await files.StoreAsync(name, "application/pdf", pdf, companyId, "generated", trusted: true, cancellationToken);

        if (email is not null)
        {
            await emailSender.SendAsync(
                new EmailMessage(
                    email.To,
                    email.Subject,
                    email.Body ?? string.Empty,
                    [new EmailAttachment(stored.File.FileName, "application/pdf", pdf)]),
                cancellationToken);
        }

        return stored.File;
    }
}
