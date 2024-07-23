#nullable enable
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime;

internal sealed class ActivationDirectory : IEnumerable<KeyValuePair<GrainId, IGrainContext>>, IAsyncDisposable, IDisposable
{
    private int _activationsCount;

    private readonly ConcurrentDictionary<GrainId, IGrainContext> _activations = new();

    public ActivationDirectory()
    {
        CatalogInstruments.RegisterActivationCountObserve(() => Count);
    }

    public int Count => _activationsCount;

    public IGrainContext? FindTarget(GrainId key)
    {
        _activations.TryGetValue(key, out var result);
        return result;
    }

    public void RecordNewTarget(IGrainContext target)
    {
        if (_activations.TryAdd(target.GrainId, target))
        {
            Interlocked.Increment(ref _activationsCount);
        }
    }

    public bool RemoveTarget(IGrainContext target)
    {
        if (_activations.TryRemove(KeyValuePair.Create(target.GrainId, target)))
        {
            Interlocked.Decrement(ref _activationsCount);
            return true;
        }

        return false;
    }

    public IEnumerator<KeyValuePair<GrainId, IGrainContext>> GetEnumerator() => _activations.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        var tasks = new List<Task>();
        foreach (var (_, value) in _activations)
        {
            try
            {
                if (value is IAsyncDisposable asyncDisposable)
                {
                    tasks.Add(asyncDisposable.DisposeAsync().AsTask());
                }
                else if (value is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
            catch
            {
                // Ignore exceptions during disposal.
            }
        }

        await Task.WhenAll(tasks).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    void IDisposable.Dispose()
    {
        foreach (var (_, value) in _activations)
        {
            try
            {
                if (value is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
            catch
            {
                // Ignore exceptions during disposal.
            }
        }
    }
}
