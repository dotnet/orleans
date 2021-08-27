using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Orleans.Runtime
{
    internal class ActivationDirectory : IEnumerable<KeyValuePair<GrainId, IGrainContext>>
    {
        private readonly ConcurrentDictionary<GrainId, IGrainContext> activations = new();                // Activation data (app grains) only.
        private readonly ConcurrentDictionary<ActivationId, SystemTarget> systemTargets = new();                // SystemTarget only.
        private readonly ConcurrentDictionary<string, CounterStatistic> systemTargetCounts = new();             // simple statistics systemTargetTypeName->count

        public int Count => activations.Count;

        public IEnumerable<SystemTarget> AllSystemTargets() => systemTargets.Select(i => i.Value);

        public IGrainContext FindTarget(GrainId key)
        {
            activations.TryGetValue(key, out var result);
            return result;
        }

        public SystemTarget FindSystemTarget(ActivationId key)
        {
            systemTargets.TryGetValue(key, out var result);
            return result;
        }

        private CounterStatistic FindSystemTargetCounter(string systemTargetTypeName)
        {
            if (systemTargetCounts.TryGetValue(systemTargetTypeName, out var ctr)) return ctr;

            var counterName = new StatisticName(StatisticNames.SYSTEM_TARGET_COUNTS, systemTargetTypeName);
            return systemTargetCounts.GetOrAdd(systemTargetTypeName, CounterStatistic.FindOrCreate(counterName, false));
        }

        public void RecordNewTarget(IGrainContext target)
        {
            activations.TryAdd(target.GrainId, target);
        }

        public void RecordNewSystemTarget(SystemTarget target)
        {
            var systemTarget = (ISystemTargetBase) target;
            systemTargets.TryAdd(target.ActivationId, target);
            if (!Constants.IsSingletonSystemTarget(systemTarget.GrainId.Type))
            {
                FindSystemTargetCounter(Constants.SystemTargetName(systemTarget.GrainId.Type)).Increment();
            }
        }

        public void RemoveSystemTarget(SystemTarget target)
        {
            var systemTarget = (ISystemTargetBase) target;
            systemTargets.TryRemove(target.ActivationId, out _);
            if (!Constants.IsSingletonSystemTarget(systemTarget.GrainId.Type))
            {
                FindSystemTargetCounter(Constants.SystemTargetName(systemTarget.GrainId.Type)).DecrementBy(1);
            }
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
            return activations.TryRemove(entry);
#else
            // Cast the dictionary to its interface type to access the explicitly implemented Remove method.
            var cacheDictionary = (IDictionary<GrainId, IGrainContext>)activations;
            return cacheDictionary.Remove(entry);
#endif
        }

        public void ForEachGrainId<T>(Action<T, GrainId> func, T context)
        {
            foreach (var pair in activations)
            {
                func(context, pair.Key);
            }
        }

        public IEnumerator<KeyValuePair<GrainId, IGrainContext>> GetEnumerator() => activations.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
