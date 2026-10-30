using Erp.Core.Domain.Data;

namespace Erp.Core.Domain.Metadata;

/// <summary>
/// Metadata definition for a single field on an object.
/// May be owned by the object's owner app or added as an extension by another app.
/// </summary>
public sealed class FieldDefinition
{
    public string Name { get; set; } = string.Empty;
    public string OwnerApp { get; set; } = string.Empty;
    public FieldType Type { get; set; } = FieldType.String;
    public string? Label { get; set; }
    public bool Required { get; set; }
    public object? DefaultValue { get; set; }
    public int? MaxLength { get; set; }
    public int? Scale { get; set; }
    public decimal? MinValue { get; set; }
    public decimal? MaxValue { get; set; }
    public string? Pattern { get; set; }

    /// <summary>
    /// Declarative condition under which this field becomes required.
    /// </summary>
    public FilterNode? RequiredWhen { get; set; }

    /// <summary>
    /// Declarative condition under which this field becomes read-only.
    /// </summary>
    public FilterNode? ReadOnlyWhen { get; set; }

    /// <summary>
    /// Declarative condition under which this field is visible in the UI.
    /// </summary>
    public FilterNode? VisibleWhen { get; set; }

    /// <summary>
    /// Field may still be edited while the record is locked by a running workflow (e.g. coding on a voucher).
    /// </summary>
    public bool EditableInWorkflow { get; set; }

    /// <summary>
    /// Target object ID (e.g. <c>core.party</c>) when <see cref="Type"/> is <see cref="FieldType.Reference"/>.
    /// </summary>
    public string? TargetObject { get; set; }

    /// <summary>
    /// Referential integrity rule when <see cref="Type"/> is <see cref="FieldType.Reference"/>.
    /// </summary>
    public RelationDeleteBehavior OnDelete { get; set; } = RelationDeleteBehavior.Restrict;

    /// <summary>
    /// Enum ID (e.g. <c>demo.channel</c>) when <see cref="Type"/> is <see cref="FieldType.Enum"/>.
    /// </summary>
    public string? EnumType { get; set; }

    /// <summary>
    /// Computes the physical PostgreSQL column name. Extension fields added by another app
    /// are prefixed with the extending app's normalized identifier to prevent collisions.
    /// </summary>
    public string GetColumnName(string objectOwnerApp)
    {
        var baseCol = IdentifierNaming.ToSnakeCase(Name);
        if (string.IsNullOrWhiteSpace(OwnerApp)
            || string.Equals(OwnerApp, objectOwnerApp, StringComparison.OrdinalIgnoreCase))
        {
            return baseCol;
        }

        var prefix = IdentifierNaming.ToSnakeCase(OwnerApp);
        return $"{prefix}_{baseCol}";
    }
}

