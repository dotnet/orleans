using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Orleans.Runtime.Scheduler;


namespace Orleans.Runtime.GrainDirectory
{
    internal class AdaptiveDirectoryCacheMaintainer<TValue> : AsynchAgent
    {
        private static readonly TimeSpan SLEEP_TIME_BETWEEN_REFRESHES = Debugger.IsAttached ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(1); // this should be something like minTTL/4

        private readonly AdaptiveGrainDirectoryCache<TValue> cache;
        private readonly LocalGrainDirectory router;
        private readonly Func<List<ActivationAddress>, TValue> updateFunc;

        private long lastNumAccesses;       // for stats
        private long lastNumHits;           // for stats

        internal AdaptiveDirectoryCacheMaintainer(
            LocalGrainDirectory router,
            AdaptiveGrainDirectoryCache<TValue> cache,
            Func<List<ActivationAddress>, TValue> updateFunc)
        {
            this.updateFunc = updateFunc;
            this.router = router;
            this.cache = cache;
            
            lastNumAccesses = 0;
            lastNumHits = 0;
            OnFault = FaultBehavior.RestartOnFault;
        }

        protected override void Run()
        {
            while (router.Running)
            {
                // Run through all cache entries and do the following:
                // 1. If the entry is not expired, skip it
                // 2. If the entry is expired and was not accessed in the last time interval -- throw it away
                // 3. If the entry is expired and was accessed in the last time interval, put into "fetch-batch-requests" list

                // At the end of the process, fetch batch requests for entries that need to be refreshed

                // Upon receiving refreshing answers, if the entry was not changed, double its expiration timer.
                // If it was changed, update the cache and reset the expiration timer.

                // this dictionary holds a map between a silo address and the list of grains that need to be refreshed
                var fetchInBatchList = new Dictionary<SiloAddress, List<GrainId>>();

                // get the list of cached grains               


                // for debug only
                int cnt1 = 0, cnt2 = 0, cnt3 = 0, cnt4 = 0;

                // run through all cache entries
                var enumerator = cache.GetStoredEntries();
                while (enumerator.MoveNext())
                {
                    var pair = enumerator.Current;
                    GrainId grain = pair.Key;
                    var entry = pair.Value;

                    SiloAddress owner = router.CalculateTargetSilo(grain);
                    if (owner == null) // Null means there's no other silo and we're shutting down, so skip this entry
                    {
                        continue;
                    }

                    if (owner.Equals(router.MyAddress))
                    {
                        // we found our owned entry in the cache -- it is not supposed to happen unless there were 
                        // changes in the membership
                        Log.Warn(ErrorCode.Runtime_Error_100185, "Grain {0} owned by {1} was found in the cache of {1}", grain, owner, owner);
                        cache.Remove(grain);
                        cnt1++;                             // for debug
                    }
                    else
                    {
                        if (entry == null)
                        {
                            // 0. If the entry was deleted in parallel, presumably due to cleanup after silo death
                            cache.Remove(grain);            // for debug
                            cnt3++;                            
                        }
                        else if (!entry.IsExpired())
                        {
                            // 1. If the entry is not expired, skip it
                            cnt2++;                         // for debug
                        }
                        else if (entry.NumAccesses == 0)
                        {
                            // 2. If the entry is expired and was not accessed in the last time interval -- throw it away
                            cache.Remove(grain);            // for debug
                            cnt3++;
                        }
                        else
                        {
                            // 3. If the entry is expired and was accessed in the last time interval, put into "fetch-batch-requests" list
                            if (!fetchInBatchList.ContainsKey(owner))
                            {
                                fetchInBatchList[owner] = new List<GrainId>();
                            }
                            fetchInBatchList[owner].Add(grain);
                            // And reset the entry's access count for next time
                            entry.NumAccesses = 0;
                            cnt4++;                         // for debug
                        }
                    }
                }

                if (Log.IsVerbose2) Log.Verbose2("Silo {0} self-owned (and removed) {1}, kept {2}, removed {3} and tries to refresh {4} grains", router.MyAddress, cnt1, cnt2, cnt3, cnt4);

                // send batch requests
                SendBatchCacheRefreshRequests(fetchInBatchList);

                ProduceStats();

                // recheck every X seconds (Consider making it a configurable parameter)
                Thread.Sleep(SLEEP_TIME_BETWEEN_REFRESHES);
            }
        }

        private void SendBatchCacheRefreshRequests(Dictionary<SiloAddress, List<GrainId>> refreshRequests)
        {
            foreach (SiloAddress silo in refreshRequests.Keys)
            {
                List<Tuple<GrainId, int>> cachedGrainAndETagList = BuildGrainAndETagList(refreshRequests[silo]);

                SiloAddress capture = silo;

                router.CacheValidationsSent.Increment();
                // Send all of the items in one large request
                var validator = InsideRuntimeClient.Current.InternalGrainFactory.GetSystemTarget<IRemoteGrainDirectory>(Constants.DirectoryCacheValidatorId, capture);
                                
                router.Scheduler.QueueTask(async () =>
                {
                    var response = await validator.LookUpMany(cachedGrainAndETagList);
                    ProcessCacheRefreshResponse(capture, response);
                }, router.CacheValidator.SchedulingContext).Ignore();

                if (Log.IsVerbose2) Log.Verbose2("Silo {0} is sending request to silo {1} with {2} entries", router.MyAddress, silo, cachedGrainAndETagList.Count);                
            }
        }

        private void ProcessCacheRefreshResponse(
            SiloAddress silo,
            IReadOnlyCollection<Tuple<GrainId, int, List<ActivationAddress>>> refreshResponse)
        {
            if (Log.IsVerbose2) Log.Verbose2("Silo {0} received ProcessCacheRefreshResponse. #Response entries {1}.", router.MyAddress, refreshResponse.Count);

            int cnt1 = 0, cnt2 = 0, cnt3 = 0;

            // pass through returned results and update the cache if needed
            foreach (Tuple<GrainId, int, List<ActivationAddress>> tuple in refreshResponse)
            {
                if (tuple.Item3 != null)
                {
                    // the server returned an updated entry
                    var updated = updateFunc(tuple.Item3);
                    cache.AddOrUpdate(tuple.Item1, updated, tuple.Item2);
                    cnt1++;
                }
                else if (tuple.Item2 == -1)
                {
                    // The server indicates that it does not own the grain anymore.
                    // It could be that by now, the cache has been already updated and contains an entry received from another server (i.e., current owner for the grain).
                    // For simplicity, we do not care about this corner case and simply remove the cache entry.
                    cache.Remove(tuple.Item1);
                    cnt2++;
                }
                else
                {
                    // The server returned only a (not -1) generation number, indicating that we hold the most
                    // updated copy of the grain's activations list. 
                    // Validate that the generation number in the request and the response are equal
                    // Contract.Assert(tuple.Item2 == refreshRequest.Find(o => o.Item1 == tuple.Item1).Item2);
                    // refresh the entry in the cache
                    cache.MarkAsFresh(tuple.Item1);
                    cnt3++;
                }
            }
            if (Log.IsVerbose2) Log.Verbose2("Silo {0} processed refresh response from {1} with {2} updated, {3} removed, {4} unchanged grains", router.MyAddress, silo, cnt1, cnt2, cnt3);
        }


        /// <summary>
        /// Gets the list of grains (all owned by the same silo) and produces a new list
        /// of tuples, where each tuple holds the grain and its generation counter currently stored in the cache
        /// </summary>
        /// <param name="grains">List of grains owned by the same silo</param>
        /// <returns>List of grains in input along with their generation counters stored in the cache </returns>
        private List<Tuple<GrainId, int>> BuildGrainAndETagList(IEnumerable<GrainId> grains)
        {
            var grainAndETagList = new List<Tuple<GrainId, int>>();

            foreach (GrainId grain in grains)
            {
                // NOTE: should this be done with TryGet? Won't Get invoke the LRU getter function?
                AdaptiveGrainDirectoryCache<TValue>.GrainDirectoryCacheEntry entry = cache.Get(grain);

                if (entry != null)
                {
                    grainAndETagList.Add(new Tuple<GrainId, int>(grain, entry.ETag));
                }
                else
                {
                    // this may happen only if the LRU cache is full and decided to drop this grain
                    // while we try to refresh it
                    Log.Warn(ErrorCode.Runtime_Error_100199, "Grain {0} disappeared from the cache during maintainance", grain);
                }
            }

            return grainAndETagList;
        }

        private void ProduceStats()
        {
            // We do not want to synchronize the access on numAccess and numHits in cache to avoid performance issues.
            // Thus we take the current reading of these fields and calculate the stats. We might miss an access or two, 
            // but it should not be matter.
            long curNumAccesses = cache.NumAccesses;
            long curNumHits = cache.NumHits;

            long numAccesses = curNumAccesses - lastNumAccesses;
            long numHits = curNumHits - lastNumHits;

            if (Log.IsVerbose2) Log.Verbose2("#accesses: {0}, hit-ratio: {1}%", numAccesses, (numHits / Math.Max(numAccesses, 0.00001)) * 100);

            lastNumAccesses = curNumAccesses;
            lastNumHits = curNumHits;
        }
    }
}
