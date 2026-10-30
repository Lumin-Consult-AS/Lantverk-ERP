namespace Erp.Core.Domain.Entities;

/// <summary>
/// Persisted metadata item (<c>object</c>, <c>enum</c>, <c>interface</c>, <c>event</c>) in <c>core.metadata_item</c>.
/// </summary>
public sealed class MetadataItem : EntityBase
{
    public string Kind { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
    public string OwnerApp { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    public string DefinitionJson { get; set; } = "{}";
}

