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

        private SiloAddress SelectSiloPowerOfK(SiloAddress[] silos)
        {
            if (silos.Length == 0)
            {
                throw new SiloUnavailableException("Unable to select a candidate because there are no compatible silos.");
            }

            // Exclude overloaded and non-compatible silos
            var relevantSilos = new List<KeyValuePair<SiloAddress, CachedLocalStat>>();
            var totalSilos = _localCache.Count;
            var compatibleSilosWithoutStats = 0;
            SiloAddress sampledCompatibleSiloWithoutStats = default;
            foreach (var silo in silos)
            {
                if (!_localCache.TryGetValue(silo, out var localSiloStat))
                {
                    compatibleSilosWithoutStats++;
                    if (compatibleSilosWithoutStats == 1 || Random.Shared.Next(compatibleSilosWithoutStats) == 0)
                    {
                        sampledCompatibleSiloWithoutStats = silo;
                    }

                    continue;
                }

                if (localSiloStat.SiloStats.IsOverloaded) continue;

                relevantSilos.Add(new(silo, localSiloStat));
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

            // Some compatible silos might not have published statistics yet.
            if (compatibleSilosWithoutStats > 0)
            {
                return sampledCompatibleSiloWithoutStats;
            }

            // All compatible silos have published stats and are overloaded.
            var allSiloStats = _localCache.ToList();
            throw new SiloUnavailableException(
                $"Unable to select a candidate from {silos.Length} compatible silos (all are overloaded). All silo stats: {Utils.EnumerableToString(allSiloStats, kvp => $"SiloAddress = {kvp.Key} -> IsOverloaded = {kvp.Value.SiloStats.IsOverloaded}, ActivationCount = {kvp.Value.ActivationCount}, RecentlyUsedActivationCount = {kvp.Value.SiloStats.RecentlyUsedActivationCount}")}");
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

            // If the cache was not populated, place locally only if this silo is compatible.
            if (_localCache.IsEmpty)
            {
                if (compatibleSilos.Contains(_localAddress))
                {
                    return _localAddress;
                }

                return SelectSiloPowerOfK(compatibleSilos);
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
