using System.Security.Cryptography;
using System.Text;

namespace Erp.Core.Infrastructure.Events;

/// <summary>
/// Signs outgoing webhooks: <c>X-Erp-Signature: sha256=hex(HMAC-SHA256(secret, "{timestamp}.{body}"))</c>.
/// Receivers recompute the signature over the same string and should reject stale timestamps.
/// </summary>
public static class WebhookSigner
{
    public const string SignatureHeader = "X-Erp-Signature";
    public const string TimestampHeader = "X-Erp-Timestamp";
    public const string EventIdHeader = "X-Erp-Event-Id";
    public const string EventNameHeader = "X-Erp-Event";

    public static string Sign(string secret, long unixTimestampSeconds, string body)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var data = Encoding.UTF8.GetBytes($"{unixTimestampSeconds}.{body}");
        return "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(key, data));
    }

    public static string GenerateSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}
