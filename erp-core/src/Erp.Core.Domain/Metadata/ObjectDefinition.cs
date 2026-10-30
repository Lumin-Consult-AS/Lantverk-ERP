namespace Erp.Core.Domain.Metadata;

/// <summary>
/// Business key definition on an object (in addition to the mandatory UUID primary key).
/// </summary>
public sealed class UniqueKeyDefinition
{
    public string Name { get; set; } = string.Empty;
    public List<string> Fields { get; set; } = [];
    public bool ScopeToCompany { get; set; } = true;
}

/// <summary>
/// Metadata definition for an object (<c>app.object</c>), owned by a single app.
/// </summary>
public sealed class ObjectDefinition
{
    public string Id { get; set; } = string.Empty;
    public string OwnerApp { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    public string Label { get; set; } = string.Empty;
    public bool IsSealed { get; set; }
    public bool IsCompanyScoped { get; set; } = true;
    public List<FieldDefinition> Fields { get; set; } = [];
    public List<UniqueKeyDefinition> Keys { get; set; } = [];

    public (string Schema, string Table) GetSchemaAndTable() => IdentifierNaming.SplitObjectId(Id);

    public string GetAuditTableName()
    {
        var (_, table) = GetSchemaAndTable();
        return $"{table}_audit";
    }
}

