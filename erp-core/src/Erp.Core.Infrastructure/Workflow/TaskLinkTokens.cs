using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Erp.Core.Infrastructure.Notifications;

namespace Erp.Core.Infrastructure.Workflow;

/// <summary>Content of a signed approve/reject link: one user may take one action on one task until it expires.</summary>
public sealed record TaskLinkPayload(Guid TenantId, Guid TaskId, Guid UserId, string Action, DateTimeOffset ExpiresAt);

/// <summary>
/// Creates and verifies the signed links sent in task e-mails (<c>base64url(payload).base64url(HMAC-SHA256)</c>).
/// The link only identifies the task, the user and the action; the action still goes through the normal task rules.
/// </summary>
public sealed class TaskLinkTokens(EmailOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string Create(Guid tenantId, Guid taskId, Guid userId, string action) =>
        Sign(new TaskLinkPayload(tenantId, taskId, userId, action, DateTimeOffset.UtcNow + options.LinkLifetime));

    public string Sign(TaskLinkPayload payload)
    {
        var body = Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions));
        return $"{body}.{Base64Url(Mac(body))}";
    }

    /// <summary>Returns the payload when the signature is valid and the link has not expired; otherwise <c>null</c>.</summary>
    public TaskLinkPayload? Verify(string? token)
    {
        var parts = token?.Split('.');
        if (parts is not { Length: 2 })
        {
            return null;
        }

        byte[] signature;
        byte[] body;
        try
        {
            signature = FromBase64Url(parts[1]);
            body = FromBase64Url(parts[0]);
        }
        catch (FormatException)
        {
            return null;
        }

        if (!CryptographicOperations.FixedTimeEquals(signature, Mac(parts[0])))
        {
            return null;
        }

        var payload = JsonSerializer.Deserialize<TaskLinkPayload>(body, JsonOptions);
        return payload is not null && payload.ExpiresAt > DateTimeOffset.UtcNow ? payload : null;
    }

    private byte[] Mac(string body) =>
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(options.LinkSigningKey), Encoding.ASCII.GetBytes(body));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        return Convert.FromBase64String(padded);
    }
}
