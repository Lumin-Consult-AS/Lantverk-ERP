using System.Collections.Concurrent;
using Erp.Core.Infrastructure.Storage;

namespace Erp.Core.Tests;

/// <summary>
/// Object store in memory with the retention semantics the platform relies on: a locked object cannot be deleted or
/// overwritten until its time has passed, and a lock can be extended but never shortened (compliance mode).
/// </summary>
public sealed class InMemoryObjectStorage : IObjectStorage
{
    private sealed record Entry(byte[] Content, string ContentType, DateTimeOffset? LockedUntil);

    private readonly ConcurrentDictionary<string, Entry> _objects = new();

    public int Count => _objects.Count;

    public Task PutAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken = default)
    {
        if (_objects.TryGetValue(key, out var existing) && existing.LockedUntil > DateTimeOffset.UtcNow)
        {
            throw new ObjectLockedException(key);
        }

        _objects[key] = new Entry(content, contentType, null);
        return Task.CompletedTask;
    }

    public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(_objects.TryGetValue(key, out var entry) ? entry.Content : null);

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        if (_objects.TryGetValue(key, out var entry))
        {
            if (entry.LockedUntil > DateTimeOffset.UtcNow)
            {
                throw new ObjectLockedException(key);
            }

            _objects.TryRemove(key, out _);
        }

        return Task.CompletedTask;
    }

    public Task LockUntilAsync(string key, DateTimeOffset retainUntil, CancellationToken cancellationToken = default)
    {
        _objects.AddOrUpdate(
            key,
            _ => throw new KeyNotFoundException(key),
            (_, entry) => entry.LockedUntil >= retainUntil ? entry : entry with { LockedUntil = retainUntil });
        return Task.CompletedTask;
    }

    public Task<DateTimeOffset?> GetLockedUntilAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(_objects.TryGetValue(key, out var entry) ? entry.LockedUntil : null);
}
