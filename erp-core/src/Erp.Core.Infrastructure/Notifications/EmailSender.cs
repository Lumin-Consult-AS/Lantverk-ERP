using System.Net.Mail;
using Microsoft.Extensions.Logging;

namespace Erp.Core.Infrastructure.Notifications;

public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>SMTP host (Mailpit in local development). When empty, e-mail is only logged.</summary>
    public string? SmtpHost { get; set; }

    public int SmtpPort { get; set; } = 1025;
    public string From { get; set; } = "noreply@erp.local";

    /// <summary>Public base URL of the core API, used to build the signed approve/reject links.</summary>
    public string PublicBaseUrl { get; set; } = "http://localhost:5000";

    /// <summary>HMAC key for the signed task-response links. Must be replaced outside development.</summary>
    public string LinkSigningKey { get; set; } = "erp-dev-task-link-signing-key-change-in-production-2026";

    public TimeSpan LinkLifetime { get; set; } = TimeSpan.FromDays(7);
}

public sealed record EmailAttachment(string FileName, string ContentType, byte[] Content);

public sealed record EmailMessage(
    string To,
    string Subject,
    string TextBody,
    IReadOnlyList<EmailAttachment>? Attachments = null);

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

/// <summary>Used when no SMTP host is configured: the message is written to the log instead of being sent.</summary>
public sealed class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        logger.LogInformation("E-mail to {To} (no SMTP host configured): {Subject}\n{Body}", message.To, message.Subject, message.TextBody);
        return Task.CompletedTask;
    }
}

public sealed class SmtpEmailSender(EmailOptions options) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        using var client = new SmtpClient(options.SmtpHost, options.SmtpPort);
        using var mail = new MailMessage(options.From, message.To, message.Subject, message.TextBody);
        var streams = new List<MemoryStream>();
        try
        {
            foreach (var attachment in message.Attachments ?? [])
            {
                var stream = new MemoryStream(attachment.Content, writable: false);
                streams.Add(stream);
                mail.Attachments.Add(new Attachment(stream, attachment.FileName, attachment.ContentType));
            }

            await client.SendMailAsync(mail, cancellationToken);
        }
        finally
        {
            streams.ForEach(s => s.Dispose());
        }
    }
}
