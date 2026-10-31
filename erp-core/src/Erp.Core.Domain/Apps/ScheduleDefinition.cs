namespace Erp.Core.Domain.Apps;

/// <summary>
/// A job the core runs for an installed app on a timetable (ADR-0025): the app's command <see cref="CommandId"/> is called
/// either every day at <see cref="At"/> (UTC) or every <see cref="EveryMinutes"/> minutes. The core does not know what the
/// command does.
/// </summary>
public sealed class ScheduleDefinition
{
    public string Id { get; set; } = string.Empty;
    public string OwnerApp { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string CommandId { get; set; } = string.Empty;

    /// <summary>Daily at this time of day, UTC (<c>HH:mm</c> in the manifest).</summary>
    public TimeOnly? At { get; set; }

    /// <summary>The <c>at</c> text as written, so a malformed value can be reported.</summary>
    public string? RawAt { get; set; }

    /// <summary>Or at this interval in minutes.</summary>
    public int? EveryMinutes { get; set; }

    /// <summary>The next time this schedule is due after <paramref name="after"/>.</summary>
    public DateTimeOffset NextAfter(DateTimeOffset after)
    {
        if (EveryMinutes is { } minutes)
        {
            return after.AddMinutes(minutes);
        }

        var time = At ?? new TimeOnly(0, 0);
        var utc = after.UtcDateTime;
        var candidate = new DateTime(utc.Year, utc.Month, utc.Day, time.Hour, time.Minute, 0, DateTimeKind.Utc);
        return new DateTimeOffset(candidate > utc ? candidate : candidate.AddDays(1), TimeSpan.Zero);
    }
}
