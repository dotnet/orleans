using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime
{
    /// <summary>
    /// Identifies activations that have been idle long enough to be deactivated.
    /// </summary>
    internal class ActivationCollector : IActivationCollector
    {
        private readonly TimeSpan quantum;
        private readonly TimeSpan shortestAgeLimit;
        private readonly ConcurrentDictionary<DateTime, Bucket> buckets;
        private readonly object nextTicketLock;
        private DateTime nextTicket;
        private static readonly List<ActivationData> nothing = new List<ActivationData> { Capacity = 0 };
        private readonly TraceLogger logger;

        public ActivationCollector(ClusterConfiguration config)
        {
            if (TimeSpan.Zero == config.Globals.CollectionQuantum)
            {
                throw new ArgumentException("Globals.CollectionQuantum cannot be zero.", "config");
            }

            quantum = config.Globals.CollectionQuantum;
            shortestAgeLimit = config.Globals.Application.ShortestCollectionAgeLimit;
            buckets = new ConcurrentDictionary<DateTime, Bucket>();
            nextTicket = MakeTicketFromDateTime(DateTime.UtcNow);
            nextTicketLock = new object();
            logger = TraceLogger.GetLogger("ActivationCollector", TraceLogger.LoggerType.Runtime);
        }

        public TimeSpan Quantum { get { return quantum; } }

        private int ApproximateCount 
        { 
            get
            {
                int sum = 0;
                foreach (var bucket in buckets.Values)
                {
                    sum += bucket.ApproximateCount;
                }
                return sum;
            } 
        }

        // Return the number of activations that were used (touched) in the last recencyPeriod.
        public int GetNumRecentlyUsed(TimeSpan recencyPeriod)
        {
            if (TimeSpan.Zero == shortestAgeLimit)
            {
                // Collection has been disabled for some types.
                return ApproximateCount;
            }

            var now = DateTime.UtcNow;
            int sum = 0;
            foreach (var bucket in buckets)
            {
                // Ticket is the date time when this bucket should be collected (last touched time plus age limit)
                // For now we take the shortest age limit as an approximation of the per-type age limit.
                DateTime ticket = bucket.Key;
                var timeTillCollection = ticket - now;
                var timeSinceLastUsed = shortestAgeLimit - timeTillCollection;
                if (timeSinceLastUsed <= recencyPeriod)
                {
                    sum += bucket.Value.ApproximateCount;
                }
            }
            return sum;
        }

        public void ScheduleCollection(ActivationData item)
        {
            lock (item)
            {
                if (item.IsExemptFromCollection)
                {
                    return;
                }

                TimeSpan timeout = item.CollectionAgeLimit;
                if (TimeSpan.Zero == timeout)
                {
                    // either the CollectionAgeLimit hasn't been initialized (will be rectified later) or it's been disabled.
                    return;
                }

                DateTime ticket = MakeTicketFromTimeSpan(timeout);
            
                if (default(DateTime) != item.CollectionTicket)
                {
                    throw new InvalidOperationException("Call CancelCollection before calling ScheduleCollection.");
                }

                Add(item, ticket);
            }
        }
        
        public bool TryCancelCollection(ActivationData item)
        {
            if (item.IsExemptFromCollection) return false;

            lock (item)
            {
                DateTime ticket = item.CollectionTicket;
                if (default(DateTime) == ticket) return false;
                if (IsExpired(ticket)) return false;

                // first, we attempt to remove the ticket. 
                Bucket bucket;
                if (!buckets.TryGetValue(ticket, out bucket) || !bucket.TryRemove(item)) return false;
            }

            return true;
        }

        public bool TryRescheduleCollection(ActivationData item)
        {
            if (item.IsExemptFromCollection) return false;

            lock (item)
            {
                if (TryRescheduleCollection_Impl(item, item.CollectionAgeLimit)) return true;

                item.ResetCollectionTicket();
                return false;
            }
        }

        private bool TryRescheduleCollection_Impl(ActivationData item, TimeSpan timeout)
        {
            // note: we expect the activation lock to be held.
            if (default(DateTime) == item.CollectionTicket) return false;
            ThrowIfTicketIsInvalid(item.CollectionTicket); 
            if (IsExpired(item.CollectionTicket)) return false;

            DateTime oldTicket = item.CollectionTicket;
            DateTime newTicket = MakeTicketFromTimeSpan(timeout);
            // if the ticket value doesn't change, then the source and destination bucket are the same and there's nothing to do.
            if (newTicket.Equals(oldTicket)) return true;

            Bucket bucket;
            if (!buckets.TryGetValue(oldTicket, out bucket) || !bucket.TryRemove(item))
            {
                // fail: item is not associated with currentKey.
                return false;
            }

            // it shouldn't be possible for Add to throw an exception here, as only one concurrent competitor should be able to reach to this point in the method.
            item.ResetCollectionTicket();
            Add(item, newTicket);
            return true;
        }

        private bool DequeueQuantum(out IEnumerable<ActivationData> items, DateTime now)
        {
            DateTime key;
            lock (nextTicketLock)
            {
                if (nextTicket > now)
                {
                    items = null;
                    return false;
                }

                key = nextTicket;
                nextTicket += quantum;
            }

            Bucket bucket;
            if (!buckets.TryRemove(key, out bucket))
            {
                items = nothing;
                return true;
            }

            items = bucket.CancelAll();
            return true;
        }

        public override string ToString()
        {
            var now = DateTime.UtcNow;
            return string.Format("<#Activations={0}, #Buckets={1}, buckets={2}>", 
                    ApproximateCount, 
                    buckets.Count,       
                    Utils.EnumerableToString(
                        buckets.Values.OrderBy(bucket => bucket.Key), bucket => (Utils.TimeSpanToString(bucket.Key - now) + "->" + bucket.ApproximateCount + " items").ToString(CultureInfo.InvariantCulture)));
        }

        /// <summary>
        /// Scans for activations that are due for collection.
        /// </summary>
        /// <returns>A list of activations that are due for collection.</returns>
        public List<ActivationData> ScanStale()
        {
            var now = DateTime.UtcNow;
            List<ActivationData> result = null;
            IEnumerable<ActivationData> activations;
            while (DequeueQuantum(out activations, now))
            {
                // at this point, all tickets associated with activations are cancelled and any attempts to reschedule will fail silently. if the activation is to be reactivated, it's our job to clear the activation's copy of the ticket.
                foreach (var activation in activations)
                {
                    lock (activation)
                    {
                        activation.ResetCollectionTicket();
                        if (activation.State != ActivationState.Valid)
                        {
                            // Do nothing: don't collect, don't reschedule.
                            // The activation can't be in Created or Activating, since we only ScheduleCollection after successfull activation.
                            // If the activation is already in Deactivating or Invalid state, its already being collected or was collected 
                            // (both mean a bug, this activation should not be in the collector)
                            // So in any state except for Valid we should just not collect and not reschedule.
                            logger.Warn(ErrorCode.Catalog_ActivationCollector_BadState_1,
                                "ActivationCollector found an activation in a non Valid state. All activation inside the ActivationCollector should be in Valid state. Activation: {0}",
                                activation.ToDetailedString());
                        }
                        else if (activation.ShouldBeKeptAlive)
                        {
                            // Consider: need to reschedule to what is the remaining time for ShouldBeKeptAlive, not the full CollectionAgeLimit.
                            ScheduleCollection(activation);
                        }
                        else if (!activation.IsInactive)
                        {
                            // This is essentialy a bug, an active activation should not be in the last bucket.
                            logger.Warn(ErrorCode.Catalog_ActivationCollector_BadState_2,
                                "ActivationCollector found an active activation in it's last bucket. This is violation of ActivationCollector invariants. " +
                                "For now going to defer it's collection. Activation: {0}",
                                activation.ToDetailedString());
                            ScheduleCollection(activation);
                        }
                        else if (!activation.IsStale(now))
                        {
                            // This is essentialy a bug, a non stale activation should not be in the last bucket.
                            logger.Warn(ErrorCode.Catalog_ActivationCollector_BadState_3,
                                "ActivationCollector found a non stale activation in it's last bucket. This is violation of ActivationCollector invariants. Now: {0}" +
                                "For now going to defer it's collection. Activation: {1}",
                                TraceLogger.PrintDate(now),
                                activation.ToDetailedString());
                            ScheduleCollection(activation);
                        }
                        else
                        {
                            // Atomically set Deactivating state, to disallow any new requests or new timer ticks to be dispatched on this activation.
                            activation.PrepareForDeactivation();
                            DecideToCollectActivation(activation, ref result);
                        }
                    }
                }
            }
            return result ?? nothing;
        }

        /// <summary>
        /// Scans for activations that have been idle for the specified age limit.
        /// </summary>
        /// <param name="ageLimit">The age limit.</param>
        /// <returns></returns>
        public List<ActivationData> ScanAll(TimeSpan ageLimit)
        {
            List<ActivationData> result = null;
            var now = DateTime.UtcNow;
            int bucketCount = buckets.Count;
            int i = 0;
            foreach (var bucket in buckets.Values)
            {
                if (i >= bucketCount) break;

                int notToExceed = bucket.ApproximateCount; 
                int j = 0;
                foreach (var activation in bucket)
                {
                    // theoretically, we could iterate forever on the ConcurrentDictionary. we limit ourselves to an approximation of the bucket's Count property to limit the number of iterations we perform.
                    if (j >= notToExceed) break;

                    lock (activation)
                    {
                        if (activation.State != ActivationState.Valid)
                        {
                            // Do nothing: don't collect, don't reschedule.
                        }
                        else if (activation.ShouldBeKeptAlive)
                        {
                            // do nothing
                        }
                        else if (!activation.IsInactive)
                        {
                            // do nothing
                        }
                        else
                        {
                            if (activation.GetIdleness(now) >= ageLimit)
                            {
                                if (bucket.TryCancel(activation))
                                {
                                    // we removed the activation from the collector. it's our responsibility to deactivate it.
                                    activation.PrepareForDeactivation();
                                    DecideToCollectActivation(activation, ref result);
                                }
                                // someone else has already deactivated the activation, so there's nothing to do.
                            }
                            else
                            {
                                // activation is not idle long enough for collection. do nothing.
                            }
                        }
                    }
                    ++j;
                }
                ++i;
            }
            return result ?? nothing;
        }

        private static void DecideToCollectActivation(ActivationData activation, ref List<ActivationData> condemned)
        {
            if (null == condemned)
            {
                condemned = new List<ActivationData> { activation };
            }
            else
            {
                condemned.Add(activation);
            }

            if (Silo.CurrentSilo.TestHook.Debug_OnDecideToCollectActivation != null)
            {
                Silo.CurrentSilo.TestHook.Debug_OnDecideToCollectActivation(activation.Grain);
            }
        }

        private static void ThrowIfTicketIsInvalid(DateTime ticket, TimeSpan quantum)
        {
            ThrowIfDefault(ticket, "ticket");
            if (0 != ticket.Ticks % quantum.Ticks)
            {
                throw new ArgumentException(string.Format("invalid ticket ({0})", ticket));
            }
        }

        private void ThrowIfTicketIsInvalid(DateTime ticket)
        {
            ThrowIfTicketIsInvalid(ticket, quantum);
        }

        private void ThrowIfExemptFromCollection(ActivationData activation, string name)
        {
            if (activation.IsExemptFromCollection)
            {
                throw new ArgumentException(string.Format("{0} should not refer to a system target or system grain.", name), name);
            }
        }

        private bool IsExpired(DateTime ticket)
        {
            return ticket < nextTicket;
        }

        private DateTime MakeTicketFromDateTime(DateTime timestamp)
        {
            // round the timestamp to the next quantum. e.g. if the quantum is 1 minute and the timestamp is 3:45:22, then the ticket will be 3:46. note that TimeStamp.Ticks and DateTime.Ticks both return a long.
            DateTime ticket = new DateTime(((timestamp.Ticks - 1) / quantum.Ticks + 1) * quantum.Ticks);
            if (ticket < nextTicket)
            {
                throw new ArgumentException(string.Format("The earliest collection that can be scheduled from now is for {0}", new DateTime(nextTicket.Ticks - quantum.Ticks + 1)));
            }
            return ticket;
        }

        private DateTime MakeTicketFromTimeSpan(TimeSpan timeout)
        {
            if (timeout < quantum)
            {
                throw new ArgumentException(String.Format("timeout must be at least {0}, but it is {1}", quantum, timeout), "timeout");
            }

            return MakeTicketFromDateTime(DateTime.UtcNow + timeout);
        }

        private void Add(ActivationData item, DateTime ticket)
        {
            // note: we expect the activation lock to be held.

            item.ResetCollectionCancelledFlag();
            Bucket bucket = 
                buckets.GetOrAdd(
                    ticket, 
                    key => 
                        new Bucket(key, quantum));
            bucket.Add(item);
            item.SetCollectionTicket(ticket);
        }

        static private void ThrowIfDefault<T>(T value, string name) where T : IEquatable<T>
        {
            if (value.Equals(default(T)))
            {
                throw new ArgumentException(string.Format("default({0}) is not allowed in this context.", typeof(T).Name), name);
            }
        }
        
        private class Bucket : IEnumerable<ActivationData>
        {
            private readonly DateTime key;
            private readonly ConcurrentDictionary<ActivationId, ActivationData> items;

            public DateTime Key { get { return key; } }
            public int ApproximateCount { get {  return items.Count; } }

            public Bucket(DateTime key, TimeSpan quantum)
            {
                ThrowIfTicketIsInvalid(key, quantum);
                this.key = key;
                items = new ConcurrentDictionary<ActivationId, ActivationData>();
            }

            public void Add(ActivationData item)
            {
                if (!items.TryAdd(item.ActivationId, item))
                {
                    throw new InvalidOperationException("item is already associated with this bucket");
                }
            }

            public bool TryRemove(ActivationData item)
            {
                if (!TryCancel(item)) return false;

                // actual removal is a memory optimization and isn't technically necessary to cancel the timeout.
                ActivationData unused;
                return items.TryRemove(item.ActivationId, out unused);
            }

            public bool TryCancel(ActivationData item)
            {
                if (!item.TrySetCollectionCancelledFlag()) return false;

                // we need to null out the ActivationData reference in the bucket in order to ensure that the memory gets collected. if we've succeeded in setting the cancellation flag, then we should have won the right to do this, so we throw an exception if we fail.
                if (items.TryUpdate(item.ActivationId, null, item)) return true;
                    
                throw new InvalidOperationException("unexpected failure to cancel deactivation");
            }

            public IEnumerable<ActivationData> CancelAll()
            {
                List<ActivationData> result = null;
                foreach (var pair in items)
                {
                    // attempt to cancel the item. if we succeed, it wasn't already cancelled and we can return it. otherwise, we silently ignore it.
                    if (pair.Value.TrySetCollectionCancelledFlag())
                    {
                        if (result == null)
                        {
                            // we only need to ensure there's enough space left for this element and any potential entries.
                            result = new List<ActivationData>();
                        }
                        result.Add(pair.Value);
                    }
                }

                return result ?? nothing;
            }

            public IEnumerator<ActivationData> GetEnumerator()
            {
                return items.Values.GetEnumerator();
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }
        }
    }
}
