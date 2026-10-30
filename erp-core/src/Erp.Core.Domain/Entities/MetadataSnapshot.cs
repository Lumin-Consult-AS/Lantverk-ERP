namespace Erp.Core.Domain.Entities;

/// <summary>
/// Immutable point-in-time snapshot of a tenant's combined metadata state (<c>core.metadata_snapshot</c>),
/// captured at every application install, upgrade, or uninstall.
/// </summary>
public sealed class MetadataSnapshot : EntityBase
{
    public int SnapshotNumber { get; set; }
    public string TriggerAction { get; set; } = string.Empty;
    public string TriggerAppId { get; set; } = string.Empty;
    public string TriggerAppVersion { get; set; } = string.Empty;
    public string SnapshotJson { get; set; } = "{}";
}

