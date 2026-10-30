namespace Erp.Core.Domain.Data;

public sealed class MetadataValidationException(string message) : Exception(message);

public sealed class RecordValidationException(string message, string? fieldName = null) : Exception(message)
{
    public string? FieldName { get; } = fieldName;
}

public sealed class ObjectNotFoundException(string objectId)
    : Exception($"Metadata object '{objectId}' is not registered.");

public sealed class RecordNotFoundException(string objectId, Guid id)
    : Exception($"Record '{id}' in object '{objectId}' was not found.");

public sealed class ConcurrencyConflictException(string objectId, Guid id, long expectedRowVersion)
    : Exception($"Optimistic concurrency conflict on '{objectId}' record '{id}' (expected row_version {expectedRowVersion}).");

