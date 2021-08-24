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

namespace Orleans.Runtime
{
    /// <summary>
    /// Identifies activations that have been idle long enough to be deactivated.
    /// </summary>
    internal class ActivationCollector : IActivationCollector, IActivationWorkingSetObserver, IHealthCheckParticipant, ILifecycleParticipant<ISiloLifecycle>
    {
        internal Action<GrainId> Debug_OnDecideToCollectActivation;
        private readonly TimeSpan quantum;
        private readonly TimeSpan shortestAgeLimit;
        private readonly ConcurrentDictionary<DateTime, Bucket> buckets = new();
        private DateTime nextTicket;
        private static readonly List<ActivationData> nothing = new(0);
        private readonly ILogger logger;
        private IAsyncTimer _collectionTimer;
        private Task _collectionLoopTask;
        private int collectionNumber;
        private int _activationCount;
        private readonly IOptions<GrainCollectionOptions> _options;
        private readonly CounterStatistic collectionCounter;

        public ActivationCollector(
            IAsyncTimerFactory timerFactory,
            IOptions<GrainCollectionOptions> options,
            ILogger<ActivationCollector> logger)
        {
            _options = options;
            collectionCounter = CounterStatistic.FindOrCreate(StatisticNames.CATALOG_ACTIVATION_COLLECTION_NUMBER_OF_COLLECTIONS);
            quantum = options.Value.CollectionQuantum;
            shortestAgeLimit = new(options.Value.ClassSpecificCollectionAge.Values.Aggregate(options.Value.CollectionAge.Ticks, (a, v) => Math.Min(a, v.Ticks)));
            nextTicket = MakeTicketFromDateTime(DateTime.UtcNow);
            this.logger = logger;
            _collectionTimer = timerFactory.Create(quantum, "Catalog.GCTimer");
        }

        // Return the number of activations that were used (touched) in the last recencyPeriod.
        public int GetNumRecentlyUsed(TimeSpan recencyPeriod)
        {
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
                    sum += bucket.Value.Items.Count;
                }
            }

            return sum;
        }

        public Task CollectActivations(TimeSpan ageLimit)
        {
            return CollectActivationsImpl(false, ageLimit);
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

        private bool DequeueQuantum(out List<ActivationData> items, DateTime now)
        {
            DateTime key;
            lock (buckets)
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
            var all = buckets.ToList();
            return string.Format("<#Activations={0}, #Buckets={1}, buckets={2}>",
                    all.Sum(b => b.Value.Items.Count),
                    all.Count,
                    Utils.EnumerableToString(
                        all.OrderBy(bucket => bucket.Key), bucket => Utils.TimeSpanToString(bucket.Key - now) + "->" + bucket.Value.Items.Count + " items"));
        }

        /// <summary>
        /// Scans for activations that are due for collection.
        /// </summary>
        /// <returns>A list of activations that are due for collection.</returns>
        public List<ActivationData> ScanStale()
        {
            var now = DateTime.UtcNow;
            List<ActivationData> condemned = null;
            while (DequeueQuantum(out var activations, now))
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
                        else if (activation.ShouldBeKeptAlive || !activation.IsInactive || !activation.IsStale(now))
                        {
                            // Consider: if ShouldBeKeptAlive is set, should reschedule to what is the remaining time for ShouldBeKeptAlive, not the full CollectionAgeLimit.
                            ScheduleCollection(activation);
                        }
                        else
                        {
                            // Atomically set Deactivating state, to disallow any new requests or new timer ticks to be dispatched on this activation.
                            activation.StartDeactivating();
                            AddActivationToList(activation, ref condemned);
                        }
                    }
                }
            }

            return condemned ?? nothing;
        }

        /// <summary>
        /// Scans for activations that have been idle for the specified age limit.
        /// </summary>
        /// <param name="ageLimit">The age limit.</param>
        /// <returns></returns>
        public List<ActivationData> ScanAll(TimeSpan ageLimit)
        {
            List<ActivationData> condemned = null;
            var now = DateTime.UtcNow;
            foreach (var kv in buckets)
            {
                var bucket = kv.Value;
                foreach (var kvp in bucket.Items)
                {
                    var activation = kvp.Value;
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
                                if (bucket.TryRemove(activation))
                                {
                                    // we removed the activation from the collector. it's our responsibility to deactivate it.
                                    activation.StartDeactivating();
                                    AddActivationToList(activation, ref condemned);
                                }
                                // someone else has already deactivated the activation, so there's nothing to do.
                            }
                            else
                            {
                                // activation is not idle long enough for collection. do nothing.
                            }
                        }
                    }
                }
            }

            return condemned ?? nothing;
        }

        private void AddActivationToList(ActivationData activation, ref List<ActivationData> condemned)
        {
            condemned ??= new();
            condemned.Add(activation);

            this.Debug_OnDecideToCollectActivation?.Invoke(activation.GrainId);
        }

        private void ThrowIfTicketIsInvalid(DateTime ticket)
        {
            if (ticket.Ticks == 0) throw new ArgumentException("Empty ticket is not allowed in this context.");
            if (0 != ticket.Ticks % quantum.Ticks)
            {
                throw new ArgumentException(string.Format("invalid ticket ({0})", ticket));
            }
        }

        private bool IsExpired(DateTime ticket)
        {
            return ticket < nextTicket;
        }

        private DateTime MakeTicketFromDateTime(DateTime timestamp)
        {
            // round the timestamp to the next quantum. e.g. if the quantum is 1 minute and the timestamp is 3:45:22, then the ticket will be 3:46. note that TimeStamp.Ticks and DateTime.Ticks both return a long.
            DateTime ticket = new DateTime(((timestamp.Ticks - 1) / quantum.Ticks + 1) * quantum.Ticks, DateTimeKind.Utc);
            if (ticket < nextTicket)
            {
                throw new ArgumentException(string.Format("The earliest collection that can be scheduled from now is for {0}", new DateTime(nextTicket.Ticks - quantum.Ticks + 1, DateTimeKind.Utc)));
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

            var bucket = buckets.GetOrAdd(ticket, _ => new Bucket());
            bucket.Add(item);
            item.SetCollectionTicket(ticket);
        }

        void IActivationWorkingSetObserver.OnAdded(IActivationWorkingSetMember member)
        {
            Interlocked.Increment(ref _activationCount);
            if (member is ActivationData activation)
            {
                if (activation.CollectionTicket == default)
                {
                    ScheduleCollection(activation);
                }
                else
                {
                    TryRescheduleCollection(activation);
                }
            }
        }

        void IActivationWorkingSetObserver.OnActive(IActivationWorkingSetMember member)
        {
            if (member is ActivationData activation)
            {
                TryRescheduleCollection(activation);
            }
        }

        void IActivationWorkingSetObserver.OnEvicted(IActivationWorkingSetMember member)
        {
            if (member is ActivationData activation && activation.CollectionTicket == default)
            {
                TryRescheduleCollection(activation);
            }
        }

        void IActivationWorkingSetObserver.OnDeactivating(IActivationWorkingSetMember member)
        {
            if (member is ActivationData activation)
            {
                TryCancelCollection(activation);
            }
        }

        void IActivationWorkingSetObserver.OnDeactivated(IActivationWorkingSetMember member)
        {
            Interlocked.Decrement(ref _activationCount);
            if (member is ActivationData activation)
            {
                TryCancelCollection(activation);
            }
        }

        private Task Start(CancellationToken cancellationToken)
        {
            _collectionLoopTask = RunActivationCollectionLoop();
            return Task.CompletedTask;
        }

        private async Task Stop(CancellationToken cancellationToken)
        {
            _collectionTimer?.Dispose();

            if (_collectionLoopTask is Task task)
            {
                await task.WithCancellation(cancellationToken);
            }
        }

        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
        {
            lifecycle.Subscribe(
                nameof(ActivationCollector),
                ServiceLifecycleStage.RuntimeServices,
                async cancellation => await Start(cancellation),
                async cancellation => await Stop(cancellation));
        }

        private async Task RunActivationCollectionLoop()
        {
            while (await _collectionTimer.NextTick())

            {
                try
                {
                    await this.CollectActivationsImpl(true);
                }
                catch (Exception exception)
                {
                    this.logger.LogError(exception, "Exception while collecting activations");
                }
            }
        }

        private async Task CollectActivationsImpl(bool scanStale, TimeSpan ageLimit = default(TimeSpan))
        {
            var watch = ValueStopwatch.StartNew();
            var number = Interlocked.Increment(ref collectionNumber);
            long memBefore = GC.GetTotalMemory(false) / (1024 * 1024);

            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(
                    (int)ErrorCode.Catalog_BeforeCollection,
                    "Before collection #{CollectionNumber}: memory: {MemoryBefore}MB, #activations: {ActivationCount}, collector: {CollectorStatus}",
                    number,
                    memBefore,
                    _activationCount,
                    ToString());
            }

            List<ActivationData> list = scanStale ? ScanStale() : ScanAll(ageLimit);
            collectionCounter.Increment();
            var count = 0;
            if (list != null && list.Count > 0)
            {
                count = list.Count;
                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("CollectActivations{0}", list.ToStrings(d => d.GrainId.ToString() + d.ActivationId));
                await DeactivateActivationsFromCollector(list);
            }
            
            long memAfter = GC.GetTotalMemory(false) / (1024 * 1024);
            watch.Stop();

            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(
                    (int)ErrorCode.Catalog_AfterCollection,
                    "After collection #{CollectionNumber} memory: {MemoryAfter}MB, #activations: {ActivationCount}, collected {CollectedCount} activations, collector: {CollectorStatus}, collection time: {CollectionTime}",
                    number,
                    memAfter,
                    _activationCount,
                    count,
                    ToString(),
                    watch.Elapsed);
            }
        }

        private async Task DeactivateActivationsFromCollector(List<ActivationData> list)
        {
            var cts = new CancellationTokenSource(_options.Value.DeactivationTimeout);
            var mtcs = new MultiTaskCompletionSource(list.Count);

            logger.Info(ErrorCode.Catalog_ShutdownActivations_1, "DeactivateActivationsFromCollector: total {0} to promptly Destroy.", list.Count);
            CounterStatistic.FindOrCreate(StatisticNames.CATALOG_ACTIVATION_SHUTDOWN_VIA_COLLECTION).IncrementBy(list.Count);

            Action<Task> signalCompletion = task => mtcs.SetOneResult();
            for (var i = 0; i < list.Count; i++)
            {
                var activationData = list[i];

                // Continue deactivation when ready
                _ = activationData.DeactivateAsync(cts.Token).ContinueWith(signalCompletion);
            }

            await mtcs.Task;
        }

        public bool CheckHealth(DateTime lastCheckTime, out string reason)
        {
            if (_collectionTimer is IAsyncTimer timer)
            {
                return timer.CheckHealth(lastCheckTime, out reason);
            }

            reason = default;
            return true;
        }

        private class Bucket
        {
            public ConcurrentDictionary<ActivationData, ActivationData> Items { get; } = new(ReferenceEqualsComparer.Default);

            public void Add(ActivationData item)
            {
                if (!Items.TryAdd(item, item))
                {
                    throw new InvalidOperationException("item is already associated with this bucket");
                }
            }

            public bool TryRemove(ActivationData item)
            {
                if (!item.TrySetCollectionCancelledFlag()) return false;
                return Items.TryRemove(item, out _);
            }

            public List<ActivationData> CancelAll()
            {
                List<ActivationData> result = null;
                foreach (var pair in Items)
                {
                    // attempt to cancel the item. if we succeed, it wasn't already cancelled and we can return it. otherwise, we silently ignore it.
                    if (pair.Value.TrySetCollectionCancelledFlag())
                    {
                        result ??= new List<ActivationData>();
                        result.Add(pair.Value);
                    }
                }

                return result ?? nothing;
            }
        }
    }
}
