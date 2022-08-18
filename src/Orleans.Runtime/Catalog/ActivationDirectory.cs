using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Orleans.Runtime
{
    internal class ActivationDirectory : IEnumerable<KeyValuePair<GrainId, IGrainContext>>
    {
        private readonly ConcurrentDictionary<GrainId, IGrainContext> _activations = new();

        public int Count => _activations.Count;

        public IEnumerable<SystemTarget> AllSystemTargets()
        {
            foreach (var kv in _activations)
            {
                if (kv.Value is SystemTarget systemTarget)
                {
                    yield return systemTarget;
                }
            }
        }

        public IGrainContext FindTarget(GrainId key)
        {
            _activations.TryGetValue(key, out var result);
            return result;
        }

        public void RecordNewTarget(IGrainContext target)
        {
            _activations.TryAdd(target.GrainId, target);
        }

        public void RemoveTarget(IGrainContext target)
        {
            if (!TryRemove(target.GrainId, target))
            {
                return;
            }
        }

        private bool TryRemove(GrainId grainId, IGrainContext target)
        {
            var entry = new KeyValuePair<GrainId, IGrainContext>(grainId, target);

#if NET5_0_OR_GREATER
            return _activations.TryRemove(entry);
#else
            // Cast the dictionary to its interface type to access the explicitly implemented Remove method.
            var cacheDictionary = (IDictionary<GrainId, IGrainContext>)activations;
            return cacheDictionary.Remove(entry);
#endif
        }

        public void ForEachGrainId<T>(Action<T, GrainId> func, T context)
        {
            foreach (var pair in _activations)
            {
                func(context, pair.Key);
            }
        }

        public IEnumerator<KeyValuePair<GrainId, IGrainContext>> GetEnumerator() => _activations.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
