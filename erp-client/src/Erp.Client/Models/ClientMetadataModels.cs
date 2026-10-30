using System.Text.Json.Serialization;

namespace Erp.Client.Models;

public sealed class ClientTokenResponse
{
    public string AccessToken { get; set; } = string.Empty;
    public int ExpiresInSeconds { get; set; }
    public Guid TenantId { get; set; }
    public string TenantName { get; set; } = string.Empty;
    public Guid? CompanyId { get; set; }
    public string? CompanyName { get; set; }
    public Guid UserId { get; set; }
    public string UserDisplayName { get; set; } = string.Empty;
    public List<string> Roles { get; set; } = [];
}

public sealed class ClientSessionInfo
{
    public ClientTenantSummary? Tenant { get; set; }
    public ClientCompanySummary? Company { get; set; }
    public ClientUserSummary? User { get; set; }
    public List<ClientInstalledAppSummary> InstalledApps { get; set; } = [];
}

public sealed class ClientTenantSummary
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string DefaultCountryCode { get; set; } = "NO";
}

public sealed class ClientCompanySummary
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string OrganizationNumber { get; set; } = string.Empty;
}

public sealed class ClientUserSummary
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public List<string> Roles { get; set; } = [];
}

public sealed class ClientInstalledAppSummary
{
    public string AppId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsDeclarative { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClientFieldType
{
    String,
    Text,
    Integer,
    Decimal,
    Boolean,
    Date,
    DateTime,
    Uuid,
    Reference,
    Enum,
    Json,
    Money,
    Blob
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClientFilterOperator
{
    Eq,
    Ne,
    Gt,
    Ge,
    Lt,
    Le,
    In,
    Contains,
    IsEmpty
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClientLogicalOperator
{
    And,
    Or
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(ClientFilterCondition), "condition")]
[JsonDerivedType(typeof(ClientFilterGroup), "group")]
public abstract class ClientFilterNode;

public sealed class ClientFilterCondition : ClientFilterNode
{
    public string Field { get; set; } = string.Empty;
    public ClientFilterOperator Operator { get; set; }
    public object? Value { get; set; }
}

public sealed class ClientFilterGroup : ClientFilterNode
{
    public ClientLogicalOperator Logical { get; set; }
    public List<ClientFilterNode> Clauses { get; set; } = [];
}

public sealed class ClientFieldDefinition
{
    public string Name { get; set; } = string.Empty;
    public string OwnerApp { get; set; } = string.Empty;
    public ClientFieldType Type { get; set; } = ClientFieldType.String;
    public string? Label { get; set; }
    public bool Required { get; set; }
    public object? DefaultValue { get; set; }
    public int? MaxLength { get; set; }
    public int? Scale { get; set; }
    public decimal? MinValue { get; set; }
    public decimal? MaxValue { get; set; }
    public string? Pattern { get; set; }
    public ClientFilterNode? RequiredWhen { get; set; }
    public ClientFilterNode? ReadOnlyWhen { get; set; }
    public ClientFilterNode? VisibleWhen { get; set; }
    public string? TargetObject { get; set; }
    public string? EnumType { get; set; }

    public string GetExposedName(string objectOwnerApp)
    {
        if (string.IsNullOrWhiteSpace(OwnerApp)
            || string.Equals(OwnerApp, objectOwnerApp, StringComparison.OrdinalIgnoreCase))
        {
            return Name;
        }

        return $"{OwnerApp}.{Name}";
    }
}

public sealed class ClientObjectStatusMachine
{
    public string Field { get; set; } = "status";
    public string Initial { get; set; } = string.Empty;
    public Dictionary<string, ClientStatusTransition> Transitions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ClientStatusTransition
{
    public string Name { get; set; } = string.Empty;
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public string? Permission { get; set; }
    public string? Workflow { get; set; }
    public bool LocksRecord { get; set; }
}

public sealed class ClientObjectDefinition
{
    public string Id { get; set; } = string.Empty;
    public string OwnerApp { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    public string Label { get; set; } = string.Empty;
    public bool IsSealed { get; set; }
    public bool IsCompanyScoped { get; set; } = true;
    public List<ClientFieldDefinition> Fields { get; set; } = [];
    public ClientObjectStatusMachine? Status { get; set; }
}

public sealed class ClientEnumDefinition
{
    public string Id { get; set; } = string.Empty;
    public string OwnerApp { get; set; } = string.Empty;
    public List<string> Values { get; set; } = [];
}

public sealed class ClientPageDefinition
{
    public string Id { get; set; } = string.Empty;
    public string OwnerApp { get; set; } = string.Empty;
    public string Type { get; set; } = "list";
    public string Object { get; set; } = string.Empty;
    public string? Title { get; set; }
    public List<string> Columns { get; set; } = [];
    public List<ClientPageGroup> Groups { get; set; } = [];
    public List<ClientPageTab> Tabs { get; set; } = [];
    public List<ClientPageAction> Actions { get; set; } = [];
}

public sealed class ClientPageGroup
{
    public string Label { get; set; } = string.Empty;
    public List<string> Fields { get; set; } = [];
}

public sealed class ClientPageTab
{
    public string Label { get; set; } = string.Empty;
    public string? Object { get; set; }
    public Dictionary<string, object?>? Filter { get; set; }
    public List<string> Fields { get; set; } = [];
}

public sealed class ClientPageAction
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Transition { get; set; }
    public string Placement { get; set; } = "primary";
}

public sealed class ClientPermissionSet
{
    public string Id { get; set; } = string.Empty;
    public string OwnerApp { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public Dictionary<string, List<string>> Grants { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ClientRecordData
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid UpdatedBy { get; set; }
    public long RowVersion { get; set; }
    public Dictionary<string, object?> Fields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ClientQueryResponse
{
    public int Count { get; set; }
    public List<ClientRecordData> Value { get; set; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClientBatchMethod
{
    Create,
    Update,
    Delete
}

public sealed class ClientBatchOperation
{
    public ClientBatchMethod Method { get; set; }
    public string ObjectId { get; set; } = string.Empty;
    public Guid? Id { get; set; }
    public long? RowVersion { get; set; }
    public Dictionary<string, object?>? Fields { get; set; }
}

