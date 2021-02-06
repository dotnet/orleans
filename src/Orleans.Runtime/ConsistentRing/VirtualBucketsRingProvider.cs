using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.ConsistentRing
{
    /// <summary>
    /// We use the 'backward/clockwise' definition to assign responsibilities on the ring. 
    /// E.g. in a ring of nodes {5, 10, 15} the responsible for key 7 is 10 (the node is responsible for its predecessing range). 
    /// The backwards/clockwise approach is consistent with many overlays, e.g., Chord, Cassandra, etc.
    /// Note: MembershipOracle uses 'forward/counter-clockwise' definition to assign responsibilities. 
    /// E.g. in a ring of nodes {5, 10, 15}, the responsible of key 7 is node 5 (the node is responsible for its sucessing range)..
    /// </summary>
    internal sealed class VirtualBucketsRingProvider :
        IConsistentRingProvider, ISiloStatusListener
    {
        private readonly List<IRingRangeListener> statusListeners = new();
        private readonly SortedDictionary<uint, SiloAddress> bucketsMap = new();
        private (uint Hash, SiloAddress SiloAddress)[] sortedBucketsList; // flattened sorted bucket list for fast lock-free calculation of CalculateTargetSilo
        private readonly ILogger logger;
        private readonly SiloAddress myAddress;
        private readonly int numBucketsPerSilo;
        private bool running;
        private IRingRange myRange;

        internal VirtualBucketsRingProvider(SiloAddress siloAddress, ILoggerFactory loggerFactory, int numVirtualBuckets)
        {
            numBucketsPerSilo = numVirtualBuckets;

            if (numBucketsPerSilo <= 0)
                throw new IndexOutOfRangeException("numBucketsPerSilo is out of the range. numBucketsPerSilo = " + numBucketsPerSilo);

            logger = loggerFactory.CreateLogger<VirtualBucketsRingProvider>();

            myAddress = siloAddress;
            running = true;
            myRange = RangeFactory.CreateFullRange();

            logger.Info("Starting {0} on silo {1}.", nameof(VirtualBucketsRingProvider), siloAddress.ToStringWithHashCode());

            StringValueStatistic.FindOrCreate(StatisticNames.CONSISTENTRING_RING, ToString);
            IntValueStatistic.FindOrCreate(StatisticNames.CONSISTENTRING_RINGSIZE, () => GetRingSize());
            StringValueStatistic.FindOrCreate(StatisticNames.CONSISTENTRING_MYRANGE_RINGDISTANCE, () => String.Format("x{0,8:X8}", ((IRingRangeInternal)myRange).RangeSize()));
            FloatValueStatistic.FindOrCreate(StatisticNames.CONSISTENTRING_MYRANGE_RINGPERCENTAGE, () => (float)((IRingRangeInternal)myRange).RangePercentage());
            FloatValueStatistic.FindOrCreate(StatisticNames.CONSISTENTRING_AVERAGERINGPERCENTAGE, () =>
            {
                int size = GetRingSize();
                return size == 0 ? 0 : ((float)100.0 / (float)size);
            });

            // add myself to the list of members
            AddServer(myAddress);
        }

        private void Stop()
        {
            running = false;
        }

        public IRingRange GetMyRange()
        {
            return myRange;
        }

        private int GetRingSize()
        {
            lock (bucketsMap)
            {
                return bucketsMap.Values.Distinct().Count();
            }
        }

        public bool SubscribeToRangeChangeEvents(IRingRangeListener observer)
        {
            lock (statusListeners)
            {
                if (statusListeners.Contains(observer)) return false;

                statusListeners.Add(observer);
                return true;
            }
        }

        public bool UnSubscribeFromRangeChangeEvents(IRingRangeListener observer)
        {
            lock (statusListeners)
            {
                return statusListeners.Remove(observer);
            }
        }

        private void NotifyLocalRangeSubscribers(IRingRange old, IRingRange now, bool increased)
        {
            logger.Info(ErrorCode.CRP_Notify, "-NotifyLocalRangeSubscribers about old {0} new {1} increased? {2}", old.ToString(), now.ToString(), increased);
            IRingRangeListener[] copy;
            lock (statusListeners)
            {
                copy = statusListeners.ToArray();
            }
            foreach (IRingRangeListener listener in copy)
            {
                try
                {
                    listener.RangeChangeNotification(old, now, increased);
                }
                catch (Exception exc)
                {
                    logger.Error(ErrorCode.CRP_Local_Subscriber_Exception,
                        String.Format("Local IRangeChangeListener {0} has thrown an exception when was notified about RangeChangeNotification about old {1} new {2} increased? {3}",
                        listener.GetType().FullName, old, now, increased), exc);
                }
            }
        }

        private void AddServer(SiloAddress silo)
        {
            var hashes = silo.GetUniformHashCodes(numBucketsPerSilo);
            lock (bucketsMap)
            {
                foreach (var hash in hashes)
                {
                    if (bucketsMap.TryGetValue(hash, out var other))
                    {
                        // If two silos conflict, take the lesser of the two (usually the older one; that is, the lower epoch)
                        if (silo.CompareTo(other) > 0) continue;
                    }
                    bucketsMap[hash] = silo;
                }

                var myOldRange = myRange;
                var myNewRange = UpdateRange();
                logger.Info(ErrorCode.CRP_Added_Silo, "Added Server {0}. Current view: {1}", silo.ToStringWithHashCode(), this.ToString());

                NotifyLocalRangeSubscribers(myOldRange, myNewRange, true);
            }
        }

        private void RemoveServer(SiloAddress silo)
        {
            lock (bucketsMap)
            {
                if (!bucketsMap.ContainsValue(silo)) return; // we have already removed this silo

                List<uint> hashes = silo.GetUniformHashCodes(numBucketsPerSilo);
                foreach (var hash in hashes)
                {
                    bucketsMap.Remove(hash);
                }

                var myOldRange = this.myRange;
                var myNewRange = UpdateRange();
                logger.Info(ErrorCode.CRP_Removed_Silo, "Removed Server {0}. Current view: {1}", silo.ToStringWithHashCode(), this.ToString());

                NotifyLocalRangeSubscribers(myOldRange, myNewRange, true);
            }
        }

        private IRingRange UpdateRange()
        {
            var bucketsList = new (uint, SiloAddress)[bucketsMap.Count];
            var idx = 0;
            foreach (var pair in bucketsMap) bucketsList[idx++] = (pair.Key, pair.Value);
            var myNewRange = CalculateRange(bucketsList, myAddress);

            // capture my range and sortedBucketsList for later lock-free access.
            myRange = myNewRange;
            sortedBucketsList = bucketsList;
            return myNewRange;
        }

        private static IRingRange CalculateRange((uint Hash, SiloAddress SiloAddress)[] list, SiloAddress silo)
        {
            var ranges = new List<IRingRange>();
            for (int i = 0; i < list.Length; i++)
            {
                var curr = list[i];
                var next = list[(i + 1) % list.Length];
                // 'backward/clockwise' definition to assign responsibilities on the ring.
                if (next.SiloAddress.Equals(silo))
                {
                    IRingRange range = RangeFactory.CreateRange(curr.Hash, next.Hash);
                    ranges.Add(range);
                }
            }
            return RangeFactory.CreateRange(ranges);
        }

        // for debugging/logging
        public override string ToString()
        {
            var sortedList = GetRanges();
            sortedList.Sort((t1, t2) => t1.Value.RangePercentage().CompareTo(t2.Value.RangePercentage()));
            return Utils.EnumerableToString(sortedList, kv => String.Format("{0} -> {1}", kv.Key, kv.Value.ToString()));
        }

        // Internal: for testing/logging only!
        internal List<(SiloAddress Key, IRingRangeInternal Value)> GetRanges()
        {
            List<SiloAddress> silos;
            (uint, SiloAddress)[] snapshotBucketsList;
            lock (bucketsMap)
            {
                silos = bucketsMap.Values.Distinct().ToList();
                snapshotBucketsList = sortedBucketsList;
            }
            var ranges = new List<(SiloAddress, IRingRangeInternal)>(silos.Count);
            foreach (var silo in silos)
            {
                var range = (IRingRangeInternal)CalculateRange(snapshotBucketsList, silo);
                ranges.Add((silo, range));
            }

            return ranges;
        }

        public void SiloStatusChangeNotification(SiloAddress updatedSilo, SiloStatus status)
        {
            // This silo's status has changed
            if (updatedSilo.Equals(myAddress))
            {
                if (status.IsTerminating())
                {
                    Stop();
                }
            }
            else // Status change for some other silo
            {
                if (status.IsTerminating())
                {
                    RemoveServer(updatedSilo);
                }
                else if (status == SiloStatus.Active)      // do not do anything with SiloStatus.Created or SiloStatus.Joining -- wait until it actually becomes active
                {
                    AddServer(updatedSilo);
                }
            }
        }

        public SiloAddress GetPrimaryTargetSilo(uint key)
        {
            return CalculateTargetSilo(key);
        }

        /// <summary>
        /// Finds the silo that owns the given hash value.
        /// This routine will always return a non-null silo address unless the excludeThisSiloIfStopping parameter is true,
        /// this is the only silo known, and this silo is stopping.
        /// </summary>
        /// <param name="hash"></param>
        /// <param name="excludeThisSiloIfStopping"></param>
        /// <returns></returns>
        private SiloAddress CalculateTargetSilo(uint hash, bool excludeThisSiloIfStopping = true)
        {
            // put a private reference to point to sortedBucketsList, 
            // so if someone is changing the sortedBucketsList reference, we won't get it changed in the middle of our operation.
            // The tricks of writing lock-free code!
            var snapshotBucketsList = sortedBucketsList;

            // excludeMySelf from being a TargetSilo if we're not running and the excludeThisSIloIfStopping flag is true. see the comment in the Stop method.
            bool excludeMySelf = excludeThisSiloIfStopping && !running;

            if (snapshotBucketsList.Length == 0)
            {
                // If the membership ring is empty, then we're the owner by default unless we're stopping.
                return excludeMySelf ? null : myAddress;
            }

            // use clockwise ... current code in membershipOracle.CalculateTargetSilo() does counter-clockwise ...
            // if you want to stick to counter-clockwise, change the responsibility definition in 'In()' method & responsibility defs in OrleansReminderMemory
            // need to implement a binary search, but for now simply traverse the list of silos sorted by their hashes
            (uint Hash, SiloAddress SiloAddress) s = default;
            foreach (var tuple in snapshotBucketsList)
            {
                if (tuple.Hash >= hash && // <= hash for counter-clockwise responsibilities
                    (!tuple.SiloAddress.Equals(myAddress) || !excludeMySelf))
                {
                    s = tuple;
                    break;
                }
            }

            if (s.SiloAddress == null)
            {
                // if not found in traversal, then first silo should be returned (we are on a ring)
                // if you go back to their counter-clockwise policy, then change the 'In()' method in OrleansReminderMemory
                s = snapshotBucketsList[0]; // vs [membershipRingList.Count - 1]; for counter-clockwise policy
                // Make sure it's not us...
                if (s.SiloAddress.Equals(myAddress) && excludeMySelf)
                {
                    // vs [membershipRingList.Count - 2]; for counter-clockwise policy
                    s = snapshotBucketsList.Length > 1 ? snapshotBucketsList[1] : default;
                }
            }
            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Calculated ring partition owner silo {0} for key {1}: {2} --> {3}", s.SiloAddress, hash, hash, s.Hash);
            return s.SiloAddress;
        }
    }
}


