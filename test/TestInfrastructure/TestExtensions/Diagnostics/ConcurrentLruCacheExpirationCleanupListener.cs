#nullable enable
using Orleans.Caching;

namespace TestExtensions;

/// <summary>
/// A test helper that subscribes to <see cref="ConcurrentLruCache{K, V}"/> expiration cleanup events.
/// </summary>
public sealed class ConcurrentLruCacheExpirationCleanupListener : IDisposable
{
    private readonly object _targetCache;
    private readonly IDisposable _subscription;
    private readonly Queue<int> _events = [];
    private readonly Queue<TaskCompletionSource<int>> _waiters = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="ConcurrentLruCacheExpirationCleanupListener"/> class.
    /// </summary>
    /// <param name="targetCache">The cache to listen for.</param>
    public ConcurrentLruCacheExpirationCleanupListener(object targetCache)
    {
        _targetCache = targetCache;
        _subscription = ConcurrentLruCacheDiagnostics.ExpiredItemsRemovedEvents.Subscribe(new Observer(this));
    }

    /// <summary>
    /// Waits for the next expiration cleanup event.
    /// </summary>
    /// <param name="timeout">The maximum time to wait. Defaults to 10 seconds.</param>
    /// <returns>The number of items removed by the cleanup.</returns>
    public Task<int> WaitForCleanupAsync(TimeSpan? timeout = null)
    {
        lock (_events)
        {
            if (_events.TryDequeue(out var removedCount))
            {
                return Task.FromResult(removedCount);
            }

            var waiter = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Enqueue(waiter);
            return waiter.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(10));
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _subscription.Dispose();

    private void OnNext(ConcurrentLruCacheDiagnostics.ExpiredItemsRemoved value)
    {
        if (!ReferenceEquals(value.Cache, _targetCache))
        {
            return;
        }

        TaskCompletionSource<int> waiter;
        lock (_events)
        {
            if (_waiters.TryDequeue(out var existingWaiter))
            {
                waiter = existingWaiter;
            }
            else
            {
                _events.Enqueue(value.RemovedCount);
                return;
            }
        }

        waiter.SetResult(value.RemovedCount);
    }

    private sealed class Observer(ConcurrentLruCacheExpirationCleanupListener listener) : IObserver<ConcurrentLruCacheDiagnostics.ExpiredItemsRemoved>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(ConcurrentLruCacheDiagnostics.ExpiredItemsRemoved value) => listener.OnNext(value);
    }
}
