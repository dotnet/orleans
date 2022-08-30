using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Orleans.Runtime
{
    internal sealed class ActivationDirectory : IEnumerable<KeyValuePair<GrainId, IGrainContext>>
    {
        private readonly ConcurrentDictionary<GrainId, IGrainContext> _activations = new();

        public int Count => _activations.Count;

        public IGrainContext FindTarget(GrainId key)
        {
            _activations.TryGetValue(key, out var result);
            return result;
        }

        public void RecordNewTarget(IGrainContext target) => _activations.TryAdd(target.GrainId, target);

        public void RemoveTarget(IGrainContext target) => _activations.TryRemove(KeyValuePair.Create(target.GrainId, target));

        public IEnumerator<KeyValuePair<GrainId, IGrainContext>> GetEnumerator() => _activations.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
