namespace Erp.Core.Domain.Metadata;

/// <summary>
/// Named enumeration definition (<c>app.enum</c>) that apps can define and extend.
/// </summary>
public sealed class EnumDefinition
{
    public string Id { get; set; } = string.Empty;
    public string OwnerApp { get; set; } = string.Empty;
    public List<string> Values { get; set; } = [];
}

/// <summary>
/// Operation contract within an <see cref="InterfaceDefinition"/>.
/// </summary>
public sealed class InterfaceOperationDefinition
{
    public string Name { get; set; } = string.Empty;
    public string? InputType { get; set; }
    public string? OutputType { get; set; }
}

/// <summary>
/// Named interface contract (<c>app.interface</c>) that an app defines and other apps implement.
/// </summary>
public sealed class InterfaceDefinition
{
    public string Id { get; set; } = string.Empty;
    public string OwnerApp { get; set; } = string.Empty;
    public List<InterfaceOperationDefinition> Operations { get; set; } = [];
}

/// <summary>
/// Versioned business event definition (<c>app.object.event@version</c>).
/// </summary>
public sealed class EventDefinition
{
    public string Id { get; set; } = string.Empty;
    public string OwnerApp { get; set; } = string.Empty;
    public int Version { get; set; } = 1;
    public List<FieldDefinition> PayloadFields { get; set; } = [];
}

/// <summary>
/// Extension of another app's object with additional fields.
/// </summary>
public sealed class ObjectExtensionDefinition
{
    public string TargetObject { get; set; } = string.Empty;
    public string ExtendingApp { get; set; } = string.Empty;
    public List<FieldDefinition> Fields { get; set; } = [];
}

/// <summary>
/// Bundle of metadata definitions parsed from YAML or JSON.
/// </summary>
public sealed class MetadataBundle
{
    public string AppId { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    public List<ObjectDefinition> Objects { get; set; } = [];
    public List<ObjectExtensionDefinition> Extensions { get; set; } = [];
    public List<EnumDefinition> Enums { get; set; } = [];
    public List<InterfaceDefinition> Interfaces { get; set; } = [];
    public List<EventDefinition> Events { get; set; } = [];
}

