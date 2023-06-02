using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Runtime.Placement
{
    internal class ActivationCountPlacementDirector : RandomPlacementDirector, ISiloStatisticsChangeListener, IPlacementDirector
    {
        private class CachedLocalStat
        {
            private int _activationCount;

            internal CachedLocalStat(SiloRuntimeStatistics siloStats) => SiloStats = siloStats;

            public SiloRuntimeStatistics SiloStats { get; }
            public int ActivationCount => _activationCount;
            public void IncrementActivationCount(int delta) => Interlocked.Add(ref _activationCount, delta);
        }
        
        // Track created activations on this silo between statistic intervals.
        private readonly ConcurrentDictionary<SiloAddress, CachedLocalStat> _localCache = new();
        private readonly SiloAddress _localAddress;
        private readonly int _chooseHowMany;

        public ActivationCountPlacementDirector(
            ILocalSiloDetails localSiloDetails,
            DeploymentLoadPublisher deploymentLoadPublisher, 
            IOptions<ActivationCountBasedPlacementOptions> options)
        {
            _localAddress = localSiloDetails.SiloAddress;
            _chooseHowMany = options.Value.ChooseOutOf;
            if (_chooseHowMany <= 0) throw new ArgumentException($"{nameof(ActivationCountBasedPlacementOptions)}.{nameof(ActivationCountBasedPlacementOptions.ChooseOutOf)} is {_chooseHowMany}. It must be greater than zero.");
            deploymentLoadPublisher?.SubscribeToStatisticsChangeEvents(this);
        }

        private static bool IsSiloOverloaded(SiloRuntimeStatistics stats) => stats.IsOverloaded || (stats.CpuUsage ?? 0) >= 100;

        private SiloAddress SelectSiloPowerOfK(SiloAddress[] silos)
        {
            var compatibleSilos = silos.ToSet();

            // Exclude overloaded and non-compatible silos
            var relevantSilos = new List<KeyValuePair<SiloAddress, CachedLocalStat>>();
            var totalSilos = 0;
            foreach (var kv in _localCache)
            {
                totalSilos++;
                if (IsSiloOverloaded(kv.Value.SiloStats)) continue;
                if (!compatibleSilos.Contains(kv.Key)) continue;

                relevantSilos.Add(kv);
            }

            if (relevantSilos.Count > 0)
            {
                int chooseFrom = Math.Min(relevantSilos.Count, _chooseHowMany);
                var chooseFromThoseSilos = new List<KeyValuePair<SiloAddress, CachedLocalStat>>(chooseFrom);
                while (chooseFromThoseSilos.Count < chooseFrom)
                {
                    int index = Random.Shared.Next(relevantSilos.Count);
                    var pickedSilo = relevantSilos[index];
                    relevantSilos.RemoveAt(index);
                    chooseFromThoseSilos.Add(pickedSilo);
                }

                KeyValuePair<SiloAddress, CachedLocalStat> minLoadedSilo = default;
                var minLoad = int.MaxValue;
                foreach (var s in chooseFromThoseSilos)
                {
                    var load = s.Value.ActivationCount + s.Value.SiloStats.RecentlyUsedActivationCount;
                    if (load < minLoad)
                    {
                        minLoadedSilo = s;
                        minLoad = load;
                    }
                }

                // Increment placement by number of silos instead of by one.
                // This is our trick to get more balanced placement, accounting to the probable
                // case when multiple silos place on the same silo at the same time, before stats are refreshed.
                minLoadedSilo.Value.IncrementActivationCount(totalSilos);

                return minLoadedSilo.Key;
            }

            // There are no compatible, non-overloaded silos.
            var all = _localCache.ToList();
            throw new SiloUnavailableException($"Unable to select a candidate from {all.Count} silos: {Utils.EnumerableToString(all, kvp => $"SiloAddress = {kvp.Key} -> {kvp.Value}")}");
        }

        public override Task<SiloAddress> OnAddActivation(PlacementStrategy strategy, PlacementTarget target, IPlacementContext context) => Task.FromResult(OnAddActivationInternal(target, context));

        private SiloAddress OnAddActivationInternal(PlacementTarget target, IPlacementContext context)
        {
            var compatibleSilos = context.GetCompatibleSilos(target);

            // If a valid placement hint was specified, use it.
            if (IPlacementDirector.GetPlacementHint(target.RequestContextData, compatibleSilos) is { } placementHint)
            {
                return placementHint;
            }

            // If the cache was not populated, just place locally
            if (_localCache.IsEmpty)
            {
                return _localAddress;
            }

            return SelectSiloPowerOfK(compatibleSilos);
        }

        public void SiloStatisticsChangeNotification(SiloAddress updatedSilo, SiloRuntimeStatistics newSiloStats)
        {
            // just create a new empty CachedLocalStat and throw the old one.
            _localCache[updatedSilo] = new(newSiloStats);
        }

        public void RemoveSilo(SiloAddress removedSilo)
        {
            _localCache.TryRemove(removedSilo, out _);
        }
    }
}
