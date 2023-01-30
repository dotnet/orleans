using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Orleans.Runtime;

internal sealed class ActivationDirectory : IEnumerable<KeyValuePair<GrainId, IGrainContext>>
{
    private int _activationsCount;

    private readonly ConcurrentDictionary<GrainId, IGrainContext> _activations = new();

    public ActivationDirectory()
    {
        CatalogInstruments.RegisterActivationCountObserve(() => Count);
    }

    public int Count => _activationsCount;

    public IGrainContext FindTarget(GrainId key)
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

    public void RemoveTarget(IGrainContext target)
    {
        if (_activations.TryRemove(KeyValuePair.Create(target.GrainId, target)))
        {
            Interlocked.Decrement(ref _activationsCount);
        }
    }

    public IEnumerator<KeyValuePair<GrainId, IGrainContext>> GetEnumerator() => _activations.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
