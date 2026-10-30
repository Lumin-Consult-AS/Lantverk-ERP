using System.Collections.Concurrent;
using System.Data.Common;

namespace Erp.Core.Infrastructure.Data;

public sealed class ActiveCallTransactionEntry(
    string correlationId,
    Guid tenantId,
    DbConnection connection,
    DbTransaction transaction) : IDisposable
{
    public string CorrelationId { get; } = correlationId;
    public Guid TenantId { get; } = tenantId;
    public DbConnection Connection { get; } = connection;
    public DbTransaction Transaction { get; } = transaction;
    public SemaphoreSlim Gate { get; } = new(1, 1);

    public void Dispose()
    {
        Gate.Dispose();
    }
}

/// <summary>
/// Tracks the active database transaction per <c>CorrelationId</c> so that any <c>DataService</c>
/// gRPC callback performed by a synchronous hook (<c>OnValidate</c>, <c>OnBeforeSave</c>, <c>OnAfterSave</c>)
/// executes inside the exact same PostgreSQL transaction as the triggering operation.
/// </summary>
public sealed class ActiveCallTransactionRegistry
{
    private readonly ConcurrentDictionary<string, ActiveCallTransactionEntry> _entries = new(StringComparer.Ordinal);
    private static readonly AsyncLocal<string?> CurrentCorrelationIdLocal = new();

    public string? CurrentCorrelationId
    {
        get => CurrentCorrelationIdLocal.Value;
        set => CurrentCorrelationIdLocal.Value = value;
    }

    public IDisposable Register(
        string correlationId,
        Guid tenantId,
        DbConnection connection,
        DbTransaction transaction)
    {
        var entry = new ActiveCallTransactionEntry(correlationId, tenantId, connection, transaction);
        _entries[correlationId] = entry;
        var previousCorrelationId = CurrentCorrelationIdLocal.Value;
        CurrentCorrelationIdLocal.Value = correlationId;

        return new RegistrationScope(this, correlationId, previousCorrelationId, entry);
    }

    public ActiveCallTransactionEntry? TryGet(string? correlationId, Guid? tenantId)
    {
        var cid = correlationId ?? CurrentCorrelationIdLocal.Value;
        if (string.IsNullOrWhiteSpace(cid))
        {
            return null;
        }

        if (_entries.TryGetValue(cid, out var entry)
            && (!tenantId.HasValue || entry.TenantId == tenantId.Value))
        {
            return entry;
        }

        return null;
    }

    private sealed class RegistrationScope(
        ActiveCallTransactionRegistry registry,
        string correlationId,
        string? previousCorrelationId,
        ActiveCallTransactionEntry entry) : IDisposable
    {
        public void Dispose()
        {
            registry._entries.TryRemove(correlationId, out _);
            CurrentCorrelationIdLocal.Value = previousCorrelationId;
            entry.Dispose();
        }
    }
}

