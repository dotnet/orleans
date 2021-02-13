using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Internal;

namespace Orleans.Runtime.Placement
{
    internal class ActivationCountPlacementDirector : RandomPlacementDirector, ISiloStatisticsChangeListener, IPlacementDirector
    {
        private class CachedLocalStat
        {
            public SiloRuntimeStatistics SiloStats { get; }
            public int ActivationCount => activationCount;

            private int activationCount;

            internal CachedLocalStat(SiloRuntimeStatistics siloStats) => SiloStats = siloStats;

            public void IncrementActivationCount(int delta)
            {
                Interlocked.Add(ref activationCount, delta);
            }
        }
        
        // Track created activations on this silo between statistic intervals.
        private readonly ConcurrentDictionary<SiloAddress, CachedLocalStat> localCache = new();
        private readonly ILogger logger;
        private readonly SiloAddress localAddress;
        private readonly int chooseHowMany;

        public ActivationCountPlacementDirector(
            ILocalSiloDetails localSiloDetails,
            DeploymentLoadPublisher deploymentLoadPublisher, 
            IOptions<ActivationCountBasedPlacementOptions> options, 
            ILogger<ActivationCountPlacementDirector> logger)
        {
            this.logger = logger;
            this.localAddress = localSiloDetails.SiloAddress;

            chooseHowMany = options.Value.ChooseOutOf;
            if (chooseHowMany <= 0) throw new ArgumentException("GlobalConfig.ActivationCountBasedPlacementChooseOutOf is " + chooseHowMany);
            deploymentLoadPublisher?.SubscribeToStatisticsChangeEvents(this);
        }

        private static bool IsSiloOverloaded(SiloRuntimeStatistics stats)
        {
            return stats.IsOverloaded || stats.CpuUsage >= 100;
        }

        private Task<SiloAddress> SelectSiloPowerOfK(PlacementTarget target, IPlacementContext context)
        {
            var compatibleSilos = context.GetCompatibleSilos(target).ToSet();
            // Exclude overloaded and non-compatible silos
            var relevantSilos = new List<KeyValuePair<SiloAddress, CachedLocalStat>>();
            var totalSilos = 0;
            foreach (var kv in localCache)
            {
                totalSilos++;
                if (IsSiloOverloaded(kv.Value.SiloStats)) continue;
                if (!compatibleSilos.Contains(kv.Key)) continue;

                relevantSilos.Add(kv);
            }

            if (relevantSilos.Count > 0)
            {
                int chooseFrom = Math.Min(relevantSilos.Count, chooseHowMany);
                var chooseFromThoseSilos = new List<KeyValuePair<SiloAddress, CachedLocalStat>>();
                while (chooseFromThoseSilos.Count < chooseFrom)
                {
                    int index = ThreadSafeRandom.Next(relevantSilos.Count);
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

                return Task.FromResult(minLoadedSilo.Key);
            }

            var all = localCache.ToList();
            var debugLog = string.Format("Unable to select a candidate from {0} silos: {1}", all.Count,
                Utils.EnumerableToString(
                    all,
                    kvp => String.Format("SiloAddress = {0} -> {1}", kvp.Key.ToString(), kvp.Value.ToString())));
            logger.Warn(ErrorCode.Placement_ActivationCountBasedDirector_NoSilos, debugLog);
            throw new OrleansException(debugLog);
        }

        public override Task<SiloAddress> OnAddActivation(
            PlacementStrategy strategy, PlacementTarget target, IPlacementContext context)
        {
            // If the cache was not populated, just place locally
            if (this.localCache.IsEmpty)
                return Task.FromResult(this.localAddress);

            return SelectSiloPowerOfK(target, context);
        }

        public void SiloStatisticsChangeNotification(SiloAddress updatedSilo, SiloRuntimeStatistics newSiloStats)
        {
            // just create a new empty CachedLocalStat and throw the old one.
            localCache[updatedSilo] = new(newSiloStats);
        }

        public void RemoveSilo(SiloAddress removedSilo)
        {
            localCache.TryRemove(removedSilo, out _);
        }
    }
}
