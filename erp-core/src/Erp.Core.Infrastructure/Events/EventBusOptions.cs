namespace Erp.Core.Infrastructure.Events;

public sealed class EventBusOptions
{
    /// <summary>Events older than this are purged from <c>core.event_log</c> (ADR-0009: 90 days).</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(90);

    /// <summary>Failed attempts before a delivery is moved to the dead-letter queue.</summary>
    public int MaxAttempts { get; set; } = 8;

    public TimeSpan BaseBackoff { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan PurgeInterval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>How long a claimed delivery is reserved before another worker may retry it.</summary>
    public TimeSpan ClaimLease { get; set; } = TimeSpan.FromSeconds(60);

    public TimeSpan WebhookTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public int BatchSize { get; set; } = 20;

    public TimeSpan BackoffFor(int failedAttempts)
    {
        var factor = Math.Pow(2, Math.Max(0, failedAttempts - 1));
        var seconds = Math.Min(BaseBackoff.TotalSeconds * factor, MaxBackoff.TotalSeconds);
        return TimeSpan.FromSeconds(seconds);
    }
}
