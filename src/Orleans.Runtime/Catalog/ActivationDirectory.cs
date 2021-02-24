using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime
{
    internal class ActivationDirectory : IEnumerable<KeyValuePair<ActivationId, ActivationData>>
    {
        private readonly ILogger logger;

        private readonly ConcurrentDictionary<ActivationId, ActivationData> activations = new();                // Activation data (app grains) only.
        private readonly ConcurrentDictionary<ActivationId, SystemTarget> systemTargets = new();                // SystemTarget only.
        private readonly ConcurrentDictionary<GrainId, List<ActivationData>> grainToActivationsMap = new();     // Activation data (app grains) only.
        private readonly ConcurrentDictionary<string, CounterStatistic> grainCounts = new();                    // simple statistics type->count
        private readonly ConcurrentDictionary<string, CounterStatistic> systemTargetCounts = new();             // simple statistics systemTargetTypeName->count

        public ActivationDirectory(ILogger<ActivationDirectory> logger) => this.logger = logger;

        public int Count => activations.Count;

        public IEnumerable<SystemTarget> AllSystemTargets() => systemTargets.Select(i => i.Value);

        public ActivationData FindTarget(ActivationId key) => activations.TryGetValue(key, out var v) ? v : null;

        public SystemTarget FindSystemTarget(ActivationId key) => systemTargets.TryGetValue(key, out var v) ? v : null;

        internal void IncrementGrainCounter(string grainTypeName)
        {
            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Increment Grain Counter {0}", grainTypeName);
            CounterStatistic ctr = FindGrainCounter(grainTypeName);
            ctr.Increment();
        }

        internal void DecrementGrainCounter(string grainTypeName)
        {
            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Decrement Grain Counter {0}", grainTypeName);
            CounterStatistic ctr = FindGrainCounter(grainTypeName);
            ctr.DecrementBy(1);
        }

        private CounterStatistic FindGrainCounter(string grainTypeName)
        {
            if (grainCounts.TryGetValue(grainTypeName, out var ctr)) return ctr;

            var counterName = new StatisticName(StatisticNames.GRAIN_COUNTS_PER_GRAIN, grainTypeName);
            return grainCounts.GetOrAdd(grainTypeName, CounterStatistic.FindOrCreate(counterName, false));
        }

        private CounterStatistic FindSystemTargetCounter(string systemTargetTypeName)
        {
            if (systemTargetCounts.TryGetValue(systemTargetTypeName, out var ctr)) return ctr;

            var counterName = new StatisticName(StatisticNames.SYSTEM_TARGET_COUNTS, systemTargetTypeName);
            return systemTargetCounts.GetOrAdd(systemTargetTypeName, CounterStatistic.FindOrCreate(counterName, false));
        }

        public void RecordNewTarget(ActivationData target)
        {
            if (!activations.TryAdd(target.ActivationId, target))
                return;

            grainToActivationsMap.AddOrUpdate(target.GrainId,
                (_, t) => new() { t },
                (_, list, t) => { lock (list) list.Add(t); return list; }, target);
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

        public void RemoveTarget(ActivationData target)
        {
            if (!activations.TryRemove(target.ActivationId, out _))
                return;

            if (grainToActivationsMap.TryGetValue(target.GrainId, out var list))
            {
                lock (list)
                {
                    list.Remove(target);
                    if (list.Count == 0)
                    {
                        List<ActivationData> list2; // == list
                        if (grainToActivationsMap.TryRemove(target.GrainId, out list2))
                        {
                            lock (list2)
                            {
                                if (list2.Count > 0)
                                {
                                    grainToActivationsMap.AddOrUpdate(target.GrainId,
                                        list2,
                                        (g, list3) => { lock (list3) list3.AddRange(list2); return list3; });
                                }
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Returns null if no activations exist for this grain ID, rather than an empty list
        /// </summary>
        public List<ActivationData> FindTargets(GrainId key)
        {
            List<ActivationData> tmp;
            if (grainToActivationsMap.TryGetValue(key, out tmp))
            {
                lock (tmp)
                {
                    return tmp.ToList();
                }
            }
            return null;
        }

        public IEnumerable<KeyValuePair<string, long>> GetSimpleGrainStatistics()
        {
            return grainCounts
                .Select(s => new KeyValuePair<string, long>(s.Key, s.Value.GetCurrentValue()))
                .Where(p => p.Value > 0);
        }

        public void PrintActivationDirectory()
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                var all = activations.ToList();
                string stats = Utils.EnumerableToString(all.Select(i => i.Value).OrderBy(act => act.Name), act => string.Format("++{0}", act.DumpStatus()), Environment.NewLine);
                if (stats.Length > 0)
                {
                    logger.Info(ErrorCode.Catalog_ActivationDirectory_Statistics, $"ActivationDirectory.PrintActivationDirectory(): Size = { all.Count}, Directory:{Environment.NewLine}{stats}");
                }
            }
        }

        public IEnumerator<KeyValuePair<ActivationId, ActivationData>> GetEnumerator() => activations.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
