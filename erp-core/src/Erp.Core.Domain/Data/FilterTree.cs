namespace Erp.Core.Domain.Data;

public enum FilterOperator
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

public enum LogicalOperator
{
    And,
    Or
}

/// <summary>
/// Base node in a filter expression tree.
/// </summary>
public abstract record FilterNode;

/// <summary>
/// Leaf condition comparing a metadata field using a <see cref="FilterOperator"/>.
/// </summary>
public sealed record FilterCondition(
    string Field,
    FilterOperator Operator,
    object? Value = null) : FilterNode;

/// <summary>
/// Composite filter group combining child nodes with <c>AND</c> or <c>OR</c>.
/// </summary>
public sealed record FilterGroup(
    LogicalOperator Logical,
    IReadOnlyList<FilterNode> Clauses) : FilterNode;

public sealed record SortField(string Field, bool Descending = false);

public sealed record DataQuery(
    FilterNode? Filter = null,
    IReadOnlyList<SortField>? OrderBy = null,
    IReadOnlyList<string>? Select = null,
    int Top = 50,
    int Skip = 0);

public enum BatchMethod
{
    Create,
    Update,
    Delete
}

public sealed record BatchOperation(
    BatchMethod Method,
    string ObjectId,
    Guid? Id = null,
    long? RowVersion = null,
    Dictionary<string, object?>? Fields = null);

public sealed record BatchResultItem(
    int Index,
    BatchMethod Method,
    string ObjectId,
    Guid Id,
    RecordData? Record = null);

