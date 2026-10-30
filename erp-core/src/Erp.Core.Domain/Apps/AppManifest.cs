using Erp.Core.Domain.Metadata;
using Erp.Core.Domain.Workflow;

namespace Erp.Core.Domain.Apps;

/// <summary>
/// Represents a parsed and validated application manifest (<c>app.yaml</c>) together with
/// its packaged pages, permission sets, and localization resources.
/// </summary>
public sealed class AppManifest
{
    public string Id { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    public string Name { get; set; } = string.Empty;
    public string? Publisher { get; set; }
    public string? License { get; set; }
    public string? Sdk { get; set; }

    public Dictionary<string, string> Dependencies { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public AppRuntimeDefinition? Runtime { get; set; }
    public bool IsDeclarative => Runtime is null;

    public MetadataBundle Metadata { get; set; } = new();
    public List<EventSubscriptionDefinition> SubscribedEvents { get; set; } = [];
    public List<ManifestPageEntry> PageEntries { get; set; } = [];
    public List<PageDefinition> Pages { get; set; } = [];
    public List<PageExtensionDefinition> PageExtensions { get; set; } = [];
    public List<PermissionSetDefinition> PermissionSets { get; set; } = [];
    public List<MigrationDefinition> Migrations { get; set; } = [];
    public List<AttentionDefinition> AttentionItems { get; set; } = [];

    /// <summary>Default approval flows shipped by the app; copied into the tenant's workflow definitions on install.</summary>
    public List<WorkflowDefinition> Workflows { get; set; } = [];

    /// <summary>
    /// Localization dictionaries keyed by locale code (e.g. <c>nb</c>, <c>en</c>).
    /// </summary>
    public Dictionary<string, Dictionary<string, string>> Translations { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Determines whether an identifier (<c>app.item</c>) carries a valid prefix for this app.
    /// For a two-part app ID like <c>core.parties</c> or <c>demo.contacts</c>, both <c>core.parties.*</c>
    /// and the namespace prefix <c>core.*</c> / <c>demo.*</c> are valid.
    /// </summary>
    public bool HasValidAppPrefix(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier) || string.IsNullOrWhiteSpace(Id))
        {
            return false;
        }

        if (identifier.StartsWith($"{Id}.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var dotIndex = Id.IndexOf('.');
        var namespacePrefix = dotIndex > 0 ? Id[..dotIndex] : Id;
        return identifier.StartsWith($"{namespacePrefix}.", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class AppRuntimeDefinition
{
    public string Image { get; set; } = string.Empty;
    public AppResourceLimits? Resources { get; set; }
    public List<string> Egress { get; set; } = [];
}

public sealed class AppResourceLimits
{
    public string? Cpu { get; set; }
    public string? Memory { get; set; }
}

public sealed class EventSubscriptionDefinition
{
    public string EventId { get; set; } = string.Empty;
    public int Version { get; set; } = 1;
    public string Handler { get; set; } = string.Empty;
}

public sealed class ManifestPageEntry
{
    public string File { get; set; } = string.Empty;
    public string? Extend { get; set; }
}

public sealed class PageDefinition
{
    public string Id { get; set; } = string.Empty;
    public string OwnerApp { get; set; } = string.Empty;
    public string Type { get; set; } = "list";
    public string Object { get; set; } = string.Empty;
    public string? Title { get; set; }
    public List<string> Columns { get; set; } = [];
    public List<PageGroupDefinition> Groups { get; set; } = [];
    public List<PageTabDefinition> Tabs { get; set; } = [];
    public List<PageActionDefinition> Actions { get; set; } = [];
}

public sealed class PageGroupDefinition
{
    public string Label { get; set; } = string.Empty;
    public List<string> Fields { get; set; } = [];
}

public sealed class PageTabDefinition
{
    public string Label { get; set; } = string.Empty;
    public string? Object { get; set; }
    public Dictionary<string, object?>? Filter { get; set; }
    public List<string> Fields { get; set; } = [];
}

public sealed class PageActionDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Transition { get; set; }
    public string Placement { get; set; } = "primary";
}

public sealed class PageExtensionDefinition
{
    public string Extend { get; set; } = string.Empty;
    public string ExtendingApp { get; set; } = string.Empty;
    public List<PageTabDefinition> Tabs { get; set; } = [];
    public List<PageFieldPlacement> Fields { get; set; } = [];
}

public sealed class PageFieldPlacement
{
    public string Field { get; set; } = string.Empty;
    public string? After { get; set; }
    public string? Group { get; set; }
}

public sealed class PermissionSetDefinition
{
    public string Id { get; set; } = string.Empty;
    public string OwnerApp { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public Dictionary<string, List<string>> Grants { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class MigrationDefinition
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public string Run { get; set; } = string.Empty;
}

public sealed class AttentionDefinition
{
    public string Id { get; set; } = string.Empty;
    public string OwnerApp { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Severity { get; set; } = "info";
    public Dictionary<string, object?> Source { get; set; } = [];
    public Dictionary<string, object?>? Audience { get; set; }
    public Dictionary<string, object?>? Action { get; set; }
}

/// <summary>
/// Full immutable snapshot of a tenant's effective metadata state at an app lifecycle event.
/// </summary>
public sealed class TenantMetadataSnapshotPayload
{
    public int SnapshotNumber { get; set; }
    public DateTimeOffset CapturedAt { get; set; }
    public string TriggerAction { get; set; } = string.Empty;
    public string TriggerAppId { get; set; } = string.Empty;
    public string TriggerAppVersion { get; set; } = string.Empty;
    public List<InstalledAppSummary> InstalledApps { get; set; } = [];
    public List<ObjectDefinition> Objects { get; set; } = [];
    public List<EnumDefinition> Enums { get; set; } = [];
    public List<EventDefinition> Events { get; set; } = [];
    public List<PageDefinition> Pages { get; set; } = [];
    public List<PermissionSetDefinition> PermissionSets { get; set; } = [];
}

public sealed record InstalledAppSummary(
    string AppId,
    string Version,
    string Name,
    string? Publisher,
    bool IsDeclarative,
    string PackageSha256);

