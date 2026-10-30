using System.Globalization;
using System.Text.Json;

namespace Erp.Sdk;

/// <summary>
/// Base class for all code-based ERP apps.
/// </summary>
public abstract class AppBase
{
}

/// <summary>
/// Marker interface registering an app as an implementation provider of a contract interface
/// defined by another app (for example, a POS or freight adapter).
/// </summary>
public interface Implements<TInterface>
{
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class ImplementsAttribute(string interfaceId) : Attribute
{
    public string InterfaceId { get; } = interfaceId;
}

/// <summary>
/// Synchronous validation hook invoked before a record is saved. Has a 5-second deadline
/// and may reject the transaction by returning <see cref="ValidationResult.Error"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class OnValidateAttribute(string objectId) : Attribute
{
    public string ObjectId { get; } = objectId;
}

/// <summary>
/// Synchronous pre-save hook invoked before a record is written to PostgreSQL.
/// Has a 5-second deadline and may mutate field values on the <see cref="Record"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class OnBeforeSaveAttribute(string objectId) : Attribute
{
    public string ObjectId { get; } = objectId;
}

/// <summary>
/// Synchronous post-save hook invoked inside the same database transaction after a record is written.
/// Has a 5-second deadline.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class OnAfterSaveAttribute(string objectId) : Attribute
{
    public string ObjectId { get; } = objectId;
}

/// <summary>
/// Asynchronous event handler attribute for domain events (<c>app.object.event@version</c>).
/// Handlers must be idempotent and can never reject the originating transaction.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class OnEventAttribute(string eventSpec) : Attribute
{
    public string EventSpec { get; } = eventSpec;
}

public sealed record ValidationFailure(string Field, string Message);

public sealed class ValidationResult
{
    public static ValidationResult Ok { get; } = new(true, []);

    public bool IsValid { get; }
    public IReadOnlyList<ValidationFailure> Failures { get; }

    private ValidationResult(bool isValid, IReadOnlyList<ValidationFailure> failures)
    {
        IsValid = isValid;
        Failures = failures;
    }

    public static ValidationResult Error(string field, string message)
        => new(false, [new ValidationFailure(field, message)]);

    public static ValidationResult Errors(IEnumerable<ValidationFailure> failures)
    {
        var list = failures.ToList();
        return list.Count == 0 ? Ok : new ValidationResult(false, list);
    }
}

/// <summary>
/// Dynamic metadata-driven record representation passed to hooks and returned by <see cref="IAppDataClient"/>.
/// </summary>
public sealed class Record
{
    private readonly Dictionary<string, object?> _fields;

    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? CompanyId { get; set; }
    public long RowVersion { get; set; }
    public bool IsModified { get; private set; }

    public IReadOnlyDictionary<string, object?> Fields => _fields;

    public Record(
        Guid id = default,
        long rowVersion = 1,
        IDictionary<string, object?>? fields = null,
        Guid tenantId = default,
        Guid? companyId = null)
    {
        Id = id;
        RowVersion = rowVersion;
        TenantId = tenantId;
        CompanyId = companyId;
        _fields = fields is not null
            ? new Dictionary<string, object?>(fields, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    }

    public T? Get<T>(string field)
    {
        if (!_fields.TryGetValue(field, out var raw) || raw is null)
        {
            return default;
        }

        var unwrapped = UnwrapJsonElement(raw);
        if (unwrapped is null)
        {
            return default;
        }

        if (unwrapped is T direct)
        {
            return direct;
        }

        var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);

        if (targetType == typeof(DateOnly))
        {
            if (unwrapped is DateOnly d) return (T)(object)d;
            if (unwrapped is DateTime dt) return (T)(object)DateOnly.FromDateTime(dt);
            if (DateOnly.TryParse(Convert.ToString(unwrapped, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
            {
                return (T)(object)parsedDate;
            }
            return default;
        }

        if (targetType == typeof(DateTimeOffset))
        {
            if (unwrapped is DateTimeOffset dto) return (T)(object)dto;
            if (DateTimeOffset.TryParse(Convert.ToString(unwrapped, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedDto))
            {
                return (T)(object)parsedDto;
            }
            return default;
        }

        if (targetType == typeof(Guid))
        {
            if (unwrapped is Guid g) return (T)(object)g;
            if (Guid.TryParse(Convert.ToString(unwrapped, CultureInfo.InvariantCulture), out var parsedGuid))
            {
                return (T)(object)parsedGuid;
            }
            return default;
        }

        if (targetType == typeof(string))
        {
            return (T)(object)(Convert.ToString(unwrapped, CultureInfo.InvariantCulture) ?? string.Empty);
        }

        return (T)Convert.ChangeType(unwrapped, targetType, CultureInfo.InvariantCulture);
    }

    public void Set(string field, object? value)
    {
        _fields[field] = value;
        IsModified = true;
    }

    private static object? UnwrapJsonElement(object? value)
    {
        if (value is not JsonElement je)
        {
            return value;
        }

        return je.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => je.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => je.TryGetInt64(out var l) ? l : je.GetDecimal(),
            _ => je
        };
    }
}

public readonly record struct FilterPredicate(string Expression)
{
    public static FilterPredicate operator &(FilterPredicate left, FilterPredicate right)
        => new($"({left.Expression} and {right.Expression})");

    public static FilterPredicate operator |(FilterPredicate left, FilterPredicate right)
        => new($"({left.Expression} or {right.Expression})");

    public override string ToString() => Expression;
}

public readonly struct FilterField(string name)
{
    public string Name { get; } = name;

    public static FilterPredicate operator ==(FilterField field, Guid value)
        => new($"{field.Name} eq '{value:D}'");

    public static FilterPredicate operator !=(FilterField field, Guid value)
        => new($"{field.Name} ne '{value:D}'");

    public static FilterPredicate operator ==(FilterField field, string? value)
        => new(value is null ? $"{field.Name} isEmpty" : $"{field.Name} eq '{Escape(value)}'");

    public static FilterPredicate operator !=(FilterField field, string? value)
        => new(value is null ? $"{field.Name} ne null" : $"{field.Name} ne '{Escape(value)}'");

    public static FilterPredicate operator ==(FilterField field, object? value)
        => new(FormatComparison(field.Name, "eq", value));

    public static FilterPredicate operator !=(FilterField field, object? value)
        => new(FormatComparison(field.Name, "ne", value));

    public static FilterPredicate operator >(FilterField field, object value)
        => new(FormatComparison(field.Name, "gt", value));

    public static FilterPredicate operator <(FilterField field, object value)
        => new(FormatComparison(field.Name, "lt", value));

    public static FilterPredicate operator >=(FilterField field, object value)
        => new(FormatComparison(field.Name, "ge", value));

    public static FilterPredicate operator <=(FilterField field, object value)
        => new(FormatComparison(field.Name, "le", value));

    public override bool Equals(object? obj) => false;
    public override int GetHashCode() => Name.GetHashCode(StringComparison.OrdinalIgnoreCase);

    private static string FormatComparison(string name, string op, object? value)
    {
        if (value is null)
        {
            return op == "eq" ? $"{name} isEmpty" : $"{name} ne null";
        }

        return value switch
        {
            Guid g => $"{name} {op} '{g:D}'",
            bool b => $"{name} {op} {(b ? "true" : "false")}",
            DateOnly d => $"{name} {op} '{d:yyyy-MM-dd}'",
            IFormattable f and (int or long or decimal or double or float) =>
                $"{name} {op} {f.ToString(null, CultureInfo.InvariantCulture)}",
            _ => $"{name} {op} '{Escape(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)}'"
        };
    }

    private static string Escape(string input) => input.Replace("'", "''");
}

public sealed class RecordFilterBuilder
{
    public FilterField this[string fieldName] => new(fieldName);
}

public interface IAppDataClient
{
    Task<Record?> GetAsync(string objectId, Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Record>> QueryAsync(string objectId, string? filterExpression = null, string? orderBy = null, int top = 50, int skip = 0, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Record>> QueryAsync(string objectId, Func<RecordFilterBuilder, FilterPredicate> filter, string? orderBy = null, int top = 50, int skip = 0, CancellationToken cancellationToken = default);
    Task<Record> CreateAsync(string objectId, IDictionary<string, object?> fields, CancellationToken cancellationToken = default);
    Task<Record> UpdateAsync(string objectId, Guid id, long rowVersion, IDictionary<string, object?> fields, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(string objectId, Guid id, long rowVersion, CancellationToken cancellationToken = default);
    Task<int> DeleteWhere(string objectId, Func<RecordFilterBuilder, FilterPredicate> filter, CancellationToken cancellationToken = default);
    Task<int> DeleteWhere(string objectId, string filterExpression, CancellationToken cancellationToken = default);
}

public interface IAppEventClient
{
    Task<string> PublishAsync(string eventName, string aggregateId, object payload, CancellationToken cancellationToken = default);
}

public interface IAppWorkflowClient
{
    Task<string> StartWorkflowAsync(string workflowId, string objectId, Guid recordId, CancellationToken cancellationToken = default);
}

public interface IAppDocumentClient
{
    Task<AppDocumentFile?> GetDocumentAsync(Guid fileId, CancellationToken cancellationToken = default);

    /// <summary>Takes an unclaimed inbox item. Throws an <c>RpcException</c> with <c>Aborted</c> when someone else has it.</summary>
    Task<AppInboxItem> ClaimInboxItemAsync(Guid itemId, string claimant, CancellationToken cancellationToken = default);

    Task<AppInboxItem> ReleaseInboxItemAsync(Guid itemId, string claimant, CancellationToken cancellationToken = default);

    /// <summary>Marks a claimed item processed and records the object it resulted in.</summary>
    Task<AppInboxItem> CompleteInboxItemAsync(
        Guid itemId,
        string claimant,
        string? resultObjectId = null,
        Guid? resultRecordId = null,
        CancellationToken cancellationToken = default);

    Task<AppInboxItem> RejectInboxItemAsync(Guid itemId, string claimant, string? reason = null, CancellationToken cancellationToken = default);
}

public sealed record AppInboxItem(
    Guid Id,
    Guid InboxId,
    Guid? CompanyId,
    string Channel,
    string Sender,
    string Subject,
    string? BodyText,
    string Status,
    IReadOnlyList<Guid> FileIds,
    IReadOnlyList<Guid> DuplicateFileIds,
    string? ClaimedBy);

public sealed record AppDocumentFile(
    string FileName,
    string ContentType,
    byte[] Content,
    string Sha256);

/// <summary>
/// Execution context carried on every call from the Core platform to an App container.
/// Includes the tenant, company, triggering user, correlation ID, and short-lived access token
/// that is automatically forwarded on every callback to Core.
/// </summary>
public sealed class AppContext
{
    public required Guid TenantId { get; init; }
    public Guid? CompanyId { get; init; }
    public required Guid UserId { get; init; }
    public required string CorrelationId { get; init; }
    public required string AccessToken { get; init; }
    public required DateOnly Today { get; init; }

    public required IAppDataClient Data { get; init; }
    public required IAppEventClient Events { get; init; }
    public required IAppWorkflowClient Workflows { get; init; }
    public required IAppDocumentClient Documents { get; init; }
}

