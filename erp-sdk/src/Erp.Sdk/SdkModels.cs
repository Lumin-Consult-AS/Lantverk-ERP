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

/// <summary>
/// Marks the handler of a command declared in the manifest (<c>commands: - id: demo.contacts.setpriority</c>). Commands are
/// business actions a client queued while offline. The method takes <see cref="CommandContext"/>, <see cref="AppContext"/>
/// and a <see cref="CancellationToken"/> (all optional) and returns <c>Task&lt;CommandResult&gt;</c>. It works through the
/// data API, so hooks, validation and permissions apply exactly as for an online user; a rejection by one of them becomes
/// <see cref="CommandResult.Rejected"/> automatically.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class CommandAttribute(string id) : Attribute
{
    public string Id { get; } = id;
}

/// <summary>A record a command touches: the version the client saw when it went offline and the version now.</summary>
public sealed record CommandTarget(string ObjectId, Guid RecordId, long BaseRowVersion, long CurrentRowVersion, bool Exists)
{
    /// <summary>The record changed (or was deleted) since the client saw it.</summary>
    public bool IsStale => !Exists || CurrentRowVersion != BaseRowVersion;
}

public sealed class CommandContext
{
    public required string CommandId { get; init; }
    public required Guid RunId { get; init; }
    public required string DeviceId { get; init; }
    public required System.Text.Json.JsonElement Args { get; init; }
    public required IReadOnlyList<CommandTarget> Targets { get; init; }

    /// <summary>A target is stale and the manifest rule is <c>adjust</c>: the handler decides how to adjust.</summary>
    public bool HasConflict { get; init; }

    /// <summary>A person chose to apply this command from the manual queue despite the conflict.</summary>
    public bool Forced { get; init; }

    public T? Arg<T>(string name) =>
        Args.ValueKind == System.Text.Json.JsonValueKind.Object
        && Args.TryGetProperty(name, out var value)
        && value.ValueKind != System.Text.Json.JsonValueKind.Null
            ? value.Deserialize<T>()
            : default;
}

/// <summary>The outcome of a command: carried out, carried out in an adjusted form, or not carried out.</summary>
public sealed record CommandResult(string Outcome, string? Message = null, string? ResultJson = null)
{
    public static CommandResult Applied(string? message = null, object? result = null) =>
        new("applied", message, result is null ? null : System.Text.Json.JsonSerializer.Serialize(result));

    /// <summary>Carried out, but not exactly as asked because the data changed meanwhile (e.g. a deviation was recorded).</summary>
    public static CommandResult Adjusted(string message, object? result = null) =>
        new("adjusted", message, result is null ? null : System.Text.Json.JsonSerializer.Serialize(result));

    public static CommandResult Rejected(string message) => new("rejected", message);
}

/// <summary>
/// Marks a data migration declared in the manifest (<c>migrations: - run: Migrations.V110</c>). The method takes
/// <see cref="AppContext"/>, <see cref="MigrationState"/> and a <see cref="CancellationToken"/> (all optional) and
/// returns <c>Task&lt;MigrationStep&gt;</c>. It works through the tenant's data in batches through the data API and must
/// be idempotent: after a failure the core calls it again with the checkpoint of the last completed step.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class MigrationAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>Where a migration is: the versions it moves between and the checkpoint of the previous step, if any.</summary>
public sealed record MigrationState(string FromVersion, string ToVersion, string? Checkpoint);

/// <summary>The result of one migration step: finished, or a checkpoint to continue from.</summary>
public sealed record MigrationStep(bool Completed, int RecordsProcessed, string? Checkpoint)
{
    public static MigrationStep Done(int recordsProcessed = 0) => new(true, recordsProcessed, null);

    public static MigrationStep Continue(string checkpoint, int recordsProcessed = 0) => new(false, recordsProcessed, checkpoint);
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

