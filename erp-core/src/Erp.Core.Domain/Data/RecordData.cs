namespace Erp.Core.Domain.Data;

/// <summary>
/// Composite value for <see cref="Metadata.FieldType.Money"/> fields (amount + ISO currency code).
/// </summary>
public sealed record MoneyValue(decimal Amount, string Currency);

/// <summary>
/// Generic record representation used by the metadata-driven Data API.
/// </summary>
public sealed class RecordData
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid? CompanyId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public Guid CreatedBy { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public Guid UpdatedBy { get; init; }
    public long RowVersion { get; init; }
    public Dictionary<string, object?> Fields { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Immutable audit trail entry read from <c>&lt;table&gt;_audit</c>.
/// </summary>
public sealed record AuditEntry(
    long AuditId,
    Guid RecordId,
    Guid TenantId,
    string Operation,
    Dictionary<string, object?>? OldData,
    Dictionary<string, object?>? NewData,
    Guid ChangedBy,
    DateTimeOffset ChangedAt,
    string? Reason);

