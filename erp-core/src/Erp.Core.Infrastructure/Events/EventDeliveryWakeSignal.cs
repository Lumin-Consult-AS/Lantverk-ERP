namespace Erp.Core.Infrastructure.Events;

/// <summary>Lets request threads wake the delivery worker right after a commit instead of waiting for the next poll.</summary>
public sealed class EventDeliveryWakeSignal
{
    private readonly SemaphoreSlim _semaphore = new(0, 1);

    public void Notify()
    {
        try
        {
            _semaphore.Release();
        }
        catch (SemaphoreFullException)
        {
            // A wake-up is already pending.
        }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _semaphore.WaitAsync(timeout, cancellationToken);
}
