namespace Erp.Core.Domain.Apps;

/// <summary>
/// The records an offline client downloads: objects, each optionally narrowed by a filter. Defined by an app in its
/// manifest (<c>syncSets</c>); the core knows nothing about what the objects mean (ADR-0014).
/// </summary>
public sealed class SyncSetDefinition
{
    public string Id { get; set; } = string.Empty;
    public string OwnerApp { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public List<SyncSetObjectDefinition> Objects { get; set; } = [];
}

public sealed class SyncSetObjectDefinition
{
    public string Object { get; set; } = string.Empty;

    /// <summary>Filter expression (<c>status eq 'open'</c>); <c>{me}</c> and <c>{today}</c> are replaced. Empty = all records.</summary>
    public string? Filter { get; set; }
}

public static class CommandConflictRules
{
    /// <summary>The command is not run; the client is told what changed and must resolve it.</summary>
    public const string Reject = "reject";

    /// <summary>The app's handler runs anyway, told about the conflict, and decides how to adjust (e.g. record a deviation).</summary>
    public const string Adjust = "adjust";

    /// <summary>The command waits in a queue for a person to apply or discard it.</summary>
    public const string Manual = "manual";

    public static readonly string[] All = [Reject, Adjust, Manual];
}

/// <summary>
/// A business action an offline client can queue (e.g. <c>ConfirmPick</c>), carried out by the app's handler through the
/// data API when the client reconnects. Declared in the manifest with the rule for conflicts (ADR-0014).
/// </summary>
public sealed class CommandDefinition
{
    public string Id { get; set; } = string.Empty;
    public string OwnerApp { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;

    /// <summary>Name of the app method (<c>[Command]</c>) that carries the command out.</summary>
    public string Handler { get; set; } = string.Empty;

    /// <summary><c>reject</c>, <c>adjust</c> or <c>manual</c>.</summary>
    public string OnConflict { get; set; } = CommandConflictRules.Reject;
}
