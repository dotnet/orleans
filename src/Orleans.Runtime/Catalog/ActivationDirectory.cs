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

    // Lookup-only addresses for the same grain-service instance. Not counted, enumerated, or disposed.
    private readonly ConcurrentDictionary<GrainId, GrainId> _aliases = new();
    private readonly CatalogInstruments _catalogInstruments;

    public ActivationDirectory(CatalogInstruments catalogInstruments)
    {
        _catalogInstruments = catalogInstruments;
        catalogInstruments.RegisterActivationCountObserve();
    }

    public int Count => _activationsCount;

    public IGrainContext? FindTarget(GrainId key)
    {
        if (_activations.TryGetValue(key, out var result))
        {
            return result;
        }

        if (_aliases.TryGetValue(key, out var primaryId) && _activations.TryGetValue(primaryId, out result))
        {
            return result;
        }

        return null;
    }

    /// <summary>
    /// Publishes an additional grain id for an instance that is already recorded under <see cref="IGrainContext.GrainId"/>.
    /// Does nothing when the id is already a real activation or an alias, so an existing entry is never replaced.
    /// </summary>
    public bool RecordAlias(GrainId aliasId, IGrainContext target)
    {
        if (aliasId.Equals(target.GrainId))
        {
            return false;
        }

        if (!_activations.TryGetValue(target.GrainId, out var recorded) || !ReferenceEquals(recorded, target))
        {
            return false;
        }

        if (_activations.ContainsKey(aliasId))
        {
            return false;
        }

        return _aliases.TryAdd(aliasId, target.GrainId);
    }

    public void RecordNewTarget(IGrainContext target)
    {
        var metrics = GetMetrics(target);
        if (_activations.TryAdd(target.GrainId, target))
        {
            Interlocked.Increment(ref _activationsCount);
            metrics.OnActivationAdded();
        }
    }

    public bool RemoveTarget(IGrainContext target)
    {
        if (_activations.TryRemove(KeyValuePair.Create(target.GrainId, target)))
        {
            // Only grain-service primaries own aliases. Skip the scan on ordinary grain deactivations.
            if (target.GrainId.Type.IsGrainService())
            {
                RemoveAliases(target.GrainId);
            }

            Interlocked.Decrement(ref _activationsCount);
            GetMetrics(target).OnActivationRemoved();
            return true;
        }

        return false;
    }

    private void RemoveAliases(GrainId primaryId)
    {
        foreach (var pair in _aliases)
        {
            if (pair.Value.Equals(primaryId))
            {
                _aliases.TryRemove(pair);
            }
        }
    }

    private GrainTypeMetrics GetMetrics(IGrainContext target) => target switch
    {
        ActivationData activation => activation.Shared.GrainTypeMetrics,
        StatelessWorkerGrainContext worker => worker.GrainTypeMetrics,
        _ => _catalogInstruments.GetGrainTypeMetrics(target.GrainId.Type)
    };

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
            finally
            {
                RemoveTarget(value);
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
            finally
            {
                RemoveTarget(value);
            }
        }
    }
}
