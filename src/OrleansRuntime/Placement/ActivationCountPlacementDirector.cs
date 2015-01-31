/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Threading;

using Orleans.Runtime.Configuration;

namespace Orleans.Runtime.Placement
{
    internal class ActivationCountPlacementDirector : RandomPlacementDirector, ISiloStatisticsChangeListener
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
        internal Func<PlacementStrategy, GrainId, IPlacementContext, Task<PlacementResult>> SelectSilo;
        
        // Track created activations on this silo between statistic intervals.
        private readonly ConcurrentDictionary<SiloAddress, CachedLocalStat> localCache = new ConcurrentDictionary<SiloAddress, CachedLocalStat>();
        private readonly TraceLogger logger;
        private readonly bool useLocalCache = true;
        // For: SelectSiloPowerOfK
        private readonly SafeRandom random = new SafeRandom();
        private int chooseHowMany = 2;
        
        public ActivationCountPlacementDirector()
        {
            logger = TraceLogger.GetLogger(this.GetType().Name);
        }

        public void Initialize(GlobalConfiguration globalConfig)
        {            
            DeploymentLoadPublisher.Instance.SubscribeToStatisticsChangeEvents(this);

            SelectSilo = SelectSiloPowerOfK;
            if (globalConfig.ActivationCountBasedPlacementChooseOutOf <= 0)
                throw new ArgumentException("GlobalConfig.ActivationCountBasedPlacementChooseOutOf is " + globalConfig.ActivationCountBasedPlacementChooseOutOf);
            
            chooseHowMany = globalConfig.ActivationCountBasedPlacementChooseOutOf;
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

        private Task<PlacementResult> MakePlacement(PlacementStrategy strategy, GrainId grain, IPlacementContext context, CachedLocalStat minLoadedSilo)
        {
            // Increment placement by number of silos instead of by one.
            // This is our trick to get more balanced placement, accounting to the probable
            // case when multiple silos place on the same silo at the same time, before stats are refreshed.
            minLoadedSilo.IncrementActivationCount(localCache.Count);

            return Task.FromResult(PlacementResult.SpecifyCreation(
                minLoadedSilo.Address,
                strategy,
                context.GetGrainTypeName(grain)));
        }

        public Task<PlacementResult> SelectSiloPowerOfK(PlacementStrategy strategy, GrainId grain, IPlacementContext context)
        {
            // Exclude overloaded silos
            var relevantSilos = new List<CachedLocalStat>();
            foreach (CachedLocalStat current in localCache.Values)
            {
                if (IsSiloOverloaded(current.SiloStats)) continue;

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

                return MakePlacement(strategy, grain, context, minLoadedSilo);
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
        private Task<PlacementResult> SelectSiloGreedy(PlacementStrategy strategy, GrainId grain, IPlacementContext context)
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
                return MakePlacement(strategy, grain, context, minLoadedSilo);
            
            var debugLog = string.Format("Unable to select a candidate from {0} silos: {1}", localCache.Count, 
                Utils.EnumerableToString(
                    localCache, 
                    kvp => String.Format("SiloAddress = {0} -> {1}", kvp.Key.ToString(), kvp.Value.ToString())));
            logger.Warn(ErrorCode.Placement_ActivationCountBasedDirector_NoSilos, debugLog);
            throw new OrleansException(debugLog);
        }

        internal override Task<PlacementResult> OnAddActivation(
            PlacementStrategy strategy, GrainId grain, IPlacementContext context)
        {
            return SelectSilo(strategy, grain, context);
        }

        public void SiloStatisticsChangeNotification(SiloAddress updatedSilo, SiloRuntimeStatistics newSiloStats)
        {
            // just create a new empty CachedLocalStat and throw the old one.
            var updatedCacheEntry = new CachedLocalStat(updatedSilo, newSiloStats);
            localCache.AddOrUpdate(updatedSilo, k => updatedCacheEntry, (k, v) => updatedCacheEntry);
        }

        public void RemoveSilo(SiloAddress removedSilo)
        {
            CachedLocalStat ignore;
            localCache.TryRemove(removedSilo, out ignore);
        }
    }
}
