using System.Text.Json;
using Erp.Core.Domain.Data;

namespace Erp.Core.Infrastructure.Sync;

public sealed class SyncOptions
{
    public const string SectionName = "Sync";

    /// <summary>How long the token handed to a device at (re)connection is valid.</summary>
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromMinutes(30);

    public int MaxReservationMinutes { get; set; } = 24 * 60;
    public int DefaultPullLimit { get; set; } = 500;
    public int MaxPullLimit { get; set; } = 2000;

    /// <summary>How long change-log entries are kept; a device that has been away longer must sync from scratch.</summary>
    public TimeSpan ChangeLogRetention { get; set; } = TimeSpan.FromDays(90);
}

public sealed record SyncDeviceInfo(
    Guid Id,
    Guid UserId,
    string Name,
    string Status,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset? BlockedAt,
    string? BlockedReason,
    DateTimeOffset CreatedAt);

public sealed record SyncDeviceToken(string Token, DateTimeOffset ExpiresAt);

public sealed record SyncSetInfo(string Id, string OwnerApp, string Label, IReadOnlyList<string> Objects);

public sealed record SyncRecord(string ObjectId, RecordData Record);

public sealed record SyncRemoved(string ObjectId, Guid Id, string Reason);

/// <summary>
/// One page of a pull. <c>Cursor</c> is where the next delta pull starts once <c>HasMore</c> is false; while it is true the
/// client calls again with <c>Continuation</c> (a snapshot in progress) or the returned <c>Cursor</c> (a long delta).
/// </summary>
public sealed record SyncPullResult(
    string? Cursor,
    bool HasMore,
    string? Continuation,
    IReadOnlyList<SyncRecord> Records,
    IReadOnlyList<SyncRemoved> Removed);

public sealed record ReservationInfo(Guid Id, Guid DeviceId, string ObjectId, Guid RecordId, DateTimeOffset ExpiresAt);

public sealed record ReservationConflict(Guid RecordId, Guid? HeldByDevice, DateTimeOffset? ExpiresAt, string Reason);

public sealed record ReserveResult(
    IReadOnlyList<ReservationInfo> Reserved,
    IReadOnlyList<ReservationConflict> Conflicts);

public sealed record CommandTargetInput(string ObjectId, Guid RecordId, long BaseRowVersion);

public sealed record CommandSubmission(
    Guid Id,
    string Name,
    JsonElement Args,
    IReadOnlyList<CommandTargetInput>? Targets);

/// <summary>A record the command touches that is not as the client last saw it.</summary>
public sealed record CommandConflict(
    string ObjectId,
    Guid RecordId,
    string Reason,
    long BaseRowVersion,
    long? CurrentRowVersion,
    RecordData? Current);

public sealed record CommandOutcome(
    Guid RowId,
    Guid ClientCommandId,
    string CommandId,
    string Status,
    string? Message,
    JsonElement? Result,
    IReadOnlyList<CommandConflict>? Conflicts,
    bool Duplicate);

/// <summary>The device is blocked (or does not belong to the caller) and may not sync.</summary>
public sealed class SyncDeviceBlockedException(string message) : Exception(message);

/// <summary>The command is not in a state that allows the action (e.g. resolving one that is not in the manual queue).</summary>
public sealed class SyncCommandStateException(string message) : Exception(message);
