using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;


namespace Orleans.Runtime
{
    internal class ActivationDirectory : IEnumerable<KeyValuePair<GrainId, ActivationData>>
    {
        private readonly ILogger logger;

        private readonly ConcurrentDictionary<GrainId, ActivationData> activations;                     // Activation data (app grains) only.
        private readonly ConcurrentDictionary<GrainId, SystemTarget> systemTargets;                // SystemTarget only.
        private readonly ConcurrentDictionary<string, CounterStatistic> grainCounts;                    // simple statistics type->count
        private readonly ConcurrentDictionary<string, CounterStatistic> systemTargetCounts;             // simple statistics systemTargetTypeName->count

        public ActivationDirectory(ILogger<ActivationDirectory> logger)
        {
            activations = new ConcurrentDictionary<GrainId, ActivationData>();
            systemTargets = new ConcurrentDictionary<GrainId, SystemTarget>();
            grainCounts = new ConcurrentDictionary<string, CounterStatistic>();
            systemTargetCounts = new ConcurrentDictionary<string, CounterStatistic>();
            this.logger = logger;
        }

        public int Count
        {
            get { return activations.Count; }
        }

        public IEnumerable<SystemTarget> AllSystemTargets()
        {
            return systemTargets.Values;
        }

        public ActivationData FindTarget(GrainId key)
        {
            return activations.TryGetValue(key, out var target) ? target : null;
        }

        public SystemTarget FindSystemTarget(GrainId key)
        {
            SystemTarget target;
            return systemTargets.TryGetValue(key, out target) ? target : null;
        }

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
            CounterStatistic ctr;
            if (grainCounts.TryGetValue(grainTypeName, out ctr)) return ctr;

            var counterName = new StatisticName(StatisticNames.GRAIN_COUNTS_PER_GRAIN, grainTypeName);
            ctr = grainCounts[grainTypeName] = CounterStatistic.FindOrCreate(counterName, false);
            return ctr;
        }

        private CounterStatistic FindSystemTargetCounter(string systemTargetTypeName)
        {
            CounterStatistic ctr;
            if (systemTargetCounts.TryGetValue(systemTargetTypeName, out ctr)) return ctr;

            var counterName = new StatisticName(StatisticNames.SYSTEM_TARGET_COUNTS, systemTargetTypeName);
            ctr = systemTargetCounts[systemTargetTypeName] = CounterStatistic.FindOrCreate(counterName, false);
            return ctr;
        }

        public void RecordNewTarget(ActivationData target)
        {
            if (!activations.TryAdd(target.GrainId, target))
            {
                ThrowActivationExistsException(target.GrainId);
            }

            static void ThrowActivationExistsException(GrainId grainId) => throw new ArgumentException($"Grain {grainId} already exists.");
        }

        public void RecordNewSystemTarget(SystemTarget target)
        {
            var systemTarget = (ISystemTargetBase) target;
            var id = systemTarget.GrainId;
            systemTargets.TryAdd(id, target);
            if (!Constants.IsSingletonSystemTarget(id.Type))
            {
                FindSystemTargetCounter(Constants.SystemTargetName(id.Type)).Increment();
            }
        }

        public void RemoveSystemTarget(SystemTarget target)
        {
            var systemTarget = (ISystemTargetBase) target;
            var id = systemTarget.GrainId;
            systemTargets.TryRemove(id, out _);
            if (!Constants.IsSingletonSystemTarget(id.Type))
            {
                FindSystemTargetCounter(Constants.SystemTargetName(id.Type)).DecrementBy(1);
            }
        }

        public void RemoveTarget(ActivationData target)
        {
            activations.TryRemove(target.GrainId, out _);
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
                string stats = Utils.EnumerableToString(activations.Values.OrderBy(act => act.Name), act => string.Format("++{0}", act.DumpStatus()), Environment.NewLine);
                if (stats.Length > 0)
                {
                    logger.Info(ErrorCode.Catalog_ActivationDirectory_Statistics, $"ActivationDirectory.PrintActivationDirectory(): Size = { activations.Count}, Directory:{Environment.NewLine}{stats}");
                }
            }
        }

        public IEnumerator<KeyValuePair<GrainId, ActivationData>> GetEnumerator()
        {
            return activations.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
