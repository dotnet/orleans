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
            public SiloAddress Address { get; private set; }
            public SiloRuntimeStatistics SiloStats { get; private set; }
            public int ActivationCount { get { return activationCount; } }

            private int activationCount;

            internal CachedLocalStat(SiloAddress address, SiloRuntimeStatistics siloStats)
            {
                Address = address;
                SiloStats = siloStats;
            }

            public void IncrementActivationCount(int delta)
            {
                Interlocked.Add(ref activationCount, delta);
            }
        }


        // internal for unit tests
        internal Func<PlacementStrategy, PlacementTarget, IPlacementContext, Task<SiloAddress>> SelectSilo;
        
        // Track created activations on this silo between statistic intervals.
        private readonly ConcurrentDictionary<SiloAddress, CachedLocalStat> localCache = new ConcurrentDictionary<SiloAddress, CachedLocalStat>();
        private readonly ILogger logger;
        private readonly SiloAddress localAddress;
        private readonly bool useLocalCache = true;
        // For: SelectSiloPowerOfK
        private readonly SafeRandom random = new SafeRandom();
        private int chooseHowMany = 2;

        public ActivationCountPlacementDirector(
            ILocalSiloDetails localSiloDetails,
            DeploymentLoadPublisher deploymentLoadPublisher, 
            IOptions<ActivationCountBasedPlacementOptions> options, 
            ILogger<ActivationCountPlacementDirector> logger)
        {
            this.logger = logger;
            this.localAddress = localSiloDetails.SiloAddress;

            SelectSilo = SelectSiloPowerOfK;
            if (options.Value.ChooseOutOf <= 0)
                throw new ArgumentException(
                    "GlobalConfig.ActivationCountBasedPlacementChooseOutOf is " + options.Value.ChooseOutOf);

            chooseHowMany = options.Value.ChooseOutOf;
            deploymentLoadPublisher?.SubscribeToStatisticsChangeEvents(this);
        }

        private static bool IsSiloOverloaded(SiloRuntimeStatistics stats)
        {
            return stats.IsOverloaded || stats.CpuUsage >= 100;
        }

        private int SiloLoad_ByActivations(CachedLocalStat cachedStats)
        {
            return useLocalCache ? 
                cachedStats.ActivationCount + cachedStats.SiloStats.ActivationCount :
                cachedStats.SiloStats.ActivationCount;
        }

        private int SiloLoad_ByRecentActivations(CachedLocalStat cachedStats)
        {
            return useLocalCache ?
                cachedStats.ActivationCount + cachedStats.SiloStats.RecentlyUsedActivationCount :
                cachedStats.SiloStats.RecentlyUsedActivationCount;
        }

        private Task<SiloAddress> MakePlacement(CachedLocalStat minLoadedSilo)
        {
            // Increment placement by number of silos instead of by one.
            // This is our trick to get more balanced placement, accounting to the probable
            // case when multiple silos place on the same silo at the same time, before stats are refreshed.
            minLoadedSilo.IncrementActivationCount(localCache.Count);

            return Task.FromResult(minLoadedSilo.Address);
        }

        public Task<SiloAddress> SelectSiloPowerOfK(PlacementStrategy strategy, PlacementTarget target, IPlacementContext context)
        {
            var compatibleSilos = context.GetCompatibleSilos(target);
            // Exclude overloaded and non-compatible silos
            var relevantSilos = new List<CachedLocalStat>();
            foreach (CachedLocalStat current in localCache.Values)
            {
                if (IsSiloOverloaded(current.SiloStats)) continue;
                if (!compatibleSilos.Contains(current.Address)) continue;

                relevantSilos.Add(current);
            }

            if (relevantSilos.Count > 0)
            {
                int chooseFrom = Math.Min(relevantSilos.Count, chooseHowMany);
                var chooseFromThoseSilos = new List<CachedLocalStat>();
                while (chooseFromThoseSilos.Count < chooseFrom)
                {
                    int index = random.Next(relevantSilos.Count);
                    var pickedSilo = relevantSilos[index];
                    relevantSilos.RemoveAt(index);
                    chooseFromThoseSilos.Add(pickedSilo);
                }

                CachedLocalStat minLoadedSilo = chooseFromThoseSilos.First();
                foreach (CachedLocalStat s in chooseFromThoseSilos)
                {
                    if (SiloLoad_ByRecentActivations(s) < SiloLoad_ByRecentActivations(minLoadedSilo))
                        minLoadedSilo = s;
                }

                return MakePlacement(minLoadedSilo);
            }
            
            var debugLog = string.Format("Unable to select a candidate from {0} silos: {1}", localCache.Count,
                Utils.EnumerableToString(
                    localCache,
                    kvp => String.Format("SiloAddress = {0} -> {1}", kvp.Key.ToString(), kvp.Value.ToString())));
            logger.Warn(ErrorCode.Placement_ActivationCountBasedDirector_NoSilos, debugLog);
            throw new OrleansException(debugLog);
        }

        /// <summary>
        /// Selects the best match from list of silos, updates local statistics.
        /// </summary>
        /// <note>
        /// This is equivalent with SelectSiloPowerOfK() with chooseHowMany = #Silos
        /// </note>
        private Task<SiloAddress> SelectSiloGreedy(PlacementStrategy strategy, GrainId grain, IPlacementRuntime context)
        {
            int minLoad = int.MaxValue;
            CachedLocalStat minLoadedSilo = null;
            foreach (CachedLocalStat current in localCache.Values)
            {
                if (IsSiloOverloaded(current.SiloStats)) continue;

                int load = SiloLoad_ByRecentActivations(current);
                if (load >= minLoad) continue;

                minLoadedSilo = current;
                minLoad = load;
            }

            if (minLoadedSilo != null) 
                return MakePlacement(minLoadedSilo);
            
            var debugLog = string.Format("Unable to select a candidate from {0} silos: {1}", localCache.Count, 
                Utils.EnumerableToString(
                    localCache, 
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

            return SelectSilo(strategy, target, context);
        }

        public void SiloStatisticsChangeNotification(SiloAddress updatedSilo, SiloRuntimeStatistics newSiloStats)
        {
            // just create a new empty CachedLocalStat and throw the old one.
            var updatedCacheEntry = new CachedLocalStat(updatedSilo, newSiloStats);
            localCache.AddOrUpdate(updatedSilo, k => updatedCacheEntry, (k, v) => updatedCacheEntry);
        }

        public void RemoveSilo(SiloAddress removedSilo)
        {
            localCache.TryRemove(removedSilo, out _);
        }
    }
}
