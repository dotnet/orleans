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
    internal class ActivationCollector : IActivationWorkingSetObserver, IHealthCheckParticipant, ILifecycleParticipant<ISiloLifecycle>
    {
        internal Action<GrainId> Debug_OnDecideToCollectActivation;
        private readonly TimeSpan quantum;
        private readonly TimeSpan shortestAgeLimit;
        private readonly ConcurrentDictionary<DateTime, Bucket> buckets = new();
        private DateTime nextTicket;
        private static readonly List<ICollectibleGrainContext> nothing = new(0);
        private readonly ILogger logger;
        private readonly IAsyncTimer _collectionTimer;
        private Task _collectionLoopTask;
        private int collectionNumber;
        private int _activationCount;
        private readonly IOptions<GrainCollectionOptions> _options;

        /// <summary>
        /// Initializes a new instance of the <see cref="ActivationCollector"/> class.
        /// </summary>
        /// <param name="timerFactory">The timer factory.</param>
        /// <param name="options">The options.</param>
        /// <param name="logger">The logger.</param>
        public ActivationCollector(
            IAsyncTimerFactory timerFactory,
            IOptions<GrainCollectionOptions> options,
            ILogger<ActivationCollector> logger)
        {
            _options = options;
            quantum = options.Value.CollectionQuantum;
            shortestAgeLimit = new(options.Value.ClassSpecificCollectionAge.Values.Aggregate(options.Value.CollectionAge.Ticks, (a, v) => Math.Min(a, v.Ticks)));
            nextTicket = MakeTicketFromDateTime(DateTime.UtcNow);
            this.logger = logger;
            _collectionTimer = timerFactory.Create(quantum, "ActivationCollector");
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

        /// <summary>
        /// Collects all eligible grain activations which have been idle for at least <paramref name="ageLimit"/>.
        /// </summary>
        /// <param name="ageLimit">The age limit.</param>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        public Task CollectActivations(TimeSpan ageLimit) => CollectActivationsImpl(false, ageLimit);

        /// <summary>
        /// Schedules the provided grain context for collection if it becomes idle for the specified duration.
        /// </summary>
        /// <param name="item">
        /// The grain context.
        /// </param>
        /// <param name="timeout">
        /// The current idle collection time for the grain.
        /// </param>
        public void ScheduleCollection(ICollectibleGrainContext item, TimeSpan timeout, DateTime now)
        {
            lock (item)
            {
                if (item.IsExemptFromCollection)
                {
                    return;
                }

                DateTime ticket = MakeTicketFromTimeSpan(timeout, now);

                if (default != item.CollectionTicket)
                {
                    throw new InvalidOperationException("Call CancelCollection before calling ScheduleCollection.");
                }

                Add(item, ticket);
            }
        }

        /// <summary>
        /// Tries the cancel idle activation collection.
        /// </summary>
        /// <param name="item">The grain context.</param>
        /// <returns><see langword="true"/> if collection was canceled, <see langword="false"/> otherwise.</returns>
        public bool TryCancelCollection(ICollectibleGrainContext item)
        {
            if (item.IsExemptFromCollection) return false;

            lock (item)
            {
                DateTime ticket = item.CollectionTicket;
                if (default == ticket) return false;
                if (IsExpired(ticket)) return false;

                // first, we attempt to remove the ticket.
                Bucket bucket;
                if (!buckets.TryGetValue(ticket, out bucket) || !bucket.TryRemove(item)) return false;
            }

            return true;
        }

        /// <summary>
        /// Tries the reschedule collection.
        /// </summary>
        /// <param name="item">The grain context.</param>
        /// <returns><see langword="true"/> if collection was canceled, <see langword="false"/> otherwise.</returns>
        public bool TryRescheduleCollection(ICollectibleGrainContext item)
        {
            if (item.IsExemptFromCollection) return false;

            lock (item)
            {
                if (TryRescheduleCollection_Impl(item, item.CollectionAgeLimit)) return true;

                item.CollectionTicket = default;
                return false;
            }
        }

        private bool TryRescheduleCollection_Impl(ICollectibleGrainContext item, TimeSpan timeout)
        {
            // note: we expect the activation lock to be held.
            if (default == item.CollectionTicket) return false;
            ThrowIfTicketIsInvalid(item.CollectionTicket);
            if (IsExpired(item.CollectionTicket)) return false;

            DateTime oldTicket = item.CollectionTicket;
            DateTime newTicket = MakeTicketFromTimeSpan(timeout, DateTime.UtcNow);
            // if the ticket value doesn't change, then the source and destination bucket are the same and there's nothing to do.
            if (newTicket.Equals(oldTicket)) return true;

            Bucket bucket;
            if (!buckets.TryGetValue(oldTicket, out bucket) || !bucket.TryRemove(item))
            {
                // fail: item is not associated with currentKey.
                return false;
            }

            // it shouldn't be possible for Add to throw an exception here, as only one concurrent competitor should be able to reach to this point in the method.
            Add(item, newTicket);
            return true;
        }

        private bool DequeueQuantum(out List<ICollectibleGrainContext> items, DateTime now)
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

        /// <inheritdoc/>
        public override string ToString()
        {
            var now = DateTime.UtcNow;
            var all = buckets.ToList();
            var bucketsText = Utils.EnumerableToString(all.OrderBy(bucket => bucket.Key), bucket => $"{Utils.TimeSpanToString(bucket.Key - now)}->{bucket.Value.Items.Count} items");
            return $"<#Activations={all.Sum(b => b.Value.Items.Count)}, #Buckets={all.Count}, buckets={bucketsText}>";
        }

        /// <summary>
        /// Scans for activations that are due for collection.
        /// </summary>
        /// <returns>A list of activations that are due for collection.</returns>
        public List<ICollectibleGrainContext> ScanStale()
        {
            var now = DateTime.UtcNow;
            List<ICollectibleGrainContext> condemned = null;
            var reason = GetDeactivationReason();
            while (DequeueQuantum(out var activations, now))
            {
                // At this point, all tickets associated with activations are cancelled and any attempts to reschedule will fail silently.
                // If the activation is to be reactivated, it's our job to clear the activation's copy of the ticket.
                foreach (var activation in activations)
                {
                    lock (activation)
                    {
                        activation.CollectionTicket = default;
                        if (!activation.IsValid)
                        {
                            // Do nothing: don't collect, don't reschedule.
                            // The activation can't be in Created or Activating, since we only ScheduleCollection after successfull activation.
                            // If the activation is already in Deactivating or Invalid state, its already being collected or was collected
                            // (both mean a bug, this activation should not be in the collector)
                            // So in any state except for Valid we should just not collect and not reschedule.
                            logger.LogWarning(
                                (int)ErrorCode.Catalog_ActivationCollector_BadState_1,
                                "ActivationCollector found an activation in a non Valid state. All activation inside the ActivationCollector should be in Valid state. Activation: {Activation}",
                                activation);
                        }
                        else if (activation.KeepAliveUntil > now)
                        {
                            var keepAliveDuration = activation.KeepAliveUntil - now;
                            var timeout = TimeSpan.FromTicks(Math.Max(keepAliveDuration.Ticks, activation.CollectionAgeLimit.Ticks));
                            ScheduleCollection(activation, timeout, now);
                        }
                        else if (!activation.IsInactive || !activation.IsStale())
                        {
                            ScheduleCollection(activation, activation.CollectionAgeLimit, now);
                        }
                        else
                        {
                            // Atomically set Deactivating state, to disallow any new requests or new timer ticks to be dispatched on this activation.
                            activation.Deactivate(reason, cancellationToken: default);
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
        /// <returns>The grain activations which have been idle for at least the specified age limit.</returns>
        public List<ICollectibleGrainContext> ScanAll(TimeSpan ageLimit)
        {
            List<ICollectibleGrainContext> condemned = null;
            var now = DateTime.UtcNow;
            var reason = GetDeactivationReason();
            foreach (var kv in buckets)
            {
                var bucket = kv.Value;
                foreach (var kvp in bucket.Items)
                {
                    var activation = kvp.Value;
                    lock (activation)
                    {
                        if (!activation.IsValid)
                        {
                            // Do nothing: don't collect, don't reschedule.
                        }
                        else if (activation.KeepAliveUntil > now)
                        {
                            // do nothing
                        }
                        else if (!activation.IsInactive)
                        {
                            // do nothing
                        }
                        else
                        {
                            if (activation.GetIdleness() >= ageLimit)
                            {
                                if (bucket.TryRemove(activation))
                                {
                                    // we removed the activation from the collector. it's our responsibility to deactivate it.
                                    activation.Deactivate(reason, cancellationToken: default);
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

        private static DeactivationReason GetDeactivationReason()
        {
            var reasonText = "This activation has become idle.";
            var reason = new DeactivationReason(DeactivationReasonCode.ActivationIdle, reasonText);
            return reason;
        }

        private void AddActivationToList(ICollectibleGrainContext activation, ref List<ICollectibleGrainContext> condemned)
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
            // Round the timestamp to the next quantum. e.g. if the quantum is 1 minute and the timestamp is 3:45:22, then the ticket will be 3:46.
            // Note that TimeStamp.Ticks and DateTime.Ticks both return a long.
            var ticket = new DateTime(((timestamp.Ticks - 1) / quantum.Ticks + 1) * quantum.Ticks, DateTimeKind.Utc);
            if (ticket < nextTicket)
            {
                throw new ArgumentException(string.Format("The earliest collection that can be scheduled from now is for {0}", new DateTime(nextTicket.Ticks - quantum.Ticks + 1, DateTimeKind.Utc)));
            }

            return ticket;
        }

        private DateTime MakeTicketFromTimeSpan(TimeSpan timeout, DateTime now)
        {
            if (timeout < quantum)
            {
                throw new ArgumentException(string.Format("timeout must be at least {0}, but it is {1}", quantum, timeout), nameof(timeout));
            }

            return MakeTicketFromDateTime(now + timeout);
        }

        private void Add(ICollectibleGrainContext item, DateTime ticket)
        {
            // note: we expect the activation lock to be held.
            item.CollectionTicket = ticket;
            var bucket = buckets.GetOrAdd(ticket, _ => new Bucket());
            bucket.Add(item);
        }

        void IActivationWorkingSetObserver.OnAdded(IActivationWorkingSetMember member)
        {
            Interlocked.Increment(ref _activationCount);
            if (member is ICollectibleGrainContext activation)
            {
                if (activation.CollectionTicket == default)
                {
                    ScheduleCollection(activation, activation.CollectionAgeLimit, DateTime.UtcNow);
                }
                else
                {
                    TryRescheduleCollection(activation);
                }
            }
        }

        void IActivationWorkingSetObserver.OnActive(IActivationWorkingSetMember member)
        {
            // We do not need to do anything when a grain becomes active, since we can lazily handle it when scanning its bucket instead.
            // This reduces the amount of unnecessary work performed.
        }

        void IActivationWorkingSetObserver.OnEvicted(IActivationWorkingSetMember member)
        {
            if (member is ICollectibleGrainContext activation && activation.CollectionTicket == default)
            {
                TryRescheduleCollection(activation);
            }
        }

        void IActivationWorkingSetObserver.OnDeactivating(IActivationWorkingSetMember member)
        {
            if (member is ICollectibleGrainContext activation)
            {
                TryCancelCollection(activation);
            }
        }

        void IActivationWorkingSetObserver.OnDeactivated(IActivationWorkingSetMember member)
        {
            Interlocked.Decrement(ref _activationCount);
            if (member is ICollectibleGrainContext activation)
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

        private async Task CollectActivationsImpl(bool scanStale, TimeSpan ageLimit = default)
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

            List<ICollectibleGrainContext> list = scanStale ? ScanStale() : ScanAll(ageLimit);
            CatalogInstruments.ActivationCollections.Add(1);
            var count = 0;
            if (list != null && list.Count > 0)
            {
                count = list.Count;
                if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("CollectActivations {Activations}", list.ToStrings(d => d.GrainId.ToString() + d.ActivationId));
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

        private async Task DeactivateActivationsFromCollector(List<ICollectibleGrainContext> list)
        {
            var cts = new CancellationTokenSource(_options.Value.DeactivationTimeout);
            var mtcs = new MultiTaskCompletionSource(list.Count);

            logger.LogInformation((int)ErrorCode.Catalog_ShutdownActivations_1, "DeactivateActivationsFromCollector: total {Count} to promptly Destroy.", list.Count);
            CatalogInstruments.ActiviationShutdownViaCollection();

            void signalCompletion(Task task) => mtcs.SetOneResult();
            var reason = GetDeactivationReason();
            for (var i = 0; i < list.Count; i++)
            {
                var activationData = list[i];

                // Continue deactivation when ready
                _ = activationData.DeactivateAsync(reason, cts.Token).ContinueWith(signalCompletion);
            }

            await mtcs.Task;
        }

        /// <inheritdoc/>
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
            public ConcurrentDictionary<ICollectibleGrainContext, ICollectibleGrainContext> Items { get; } = new(ReferenceEqualsComparer.Default);

            public void Add(ICollectibleGrainContext item)
            {
                if (!Items.TryAdd(item, item))
                {
                    throw new InvalidOperationException("item is already associated with this bucket");
                }
            }

            public bool TryRemove(ICollectibleGrainContext item)
            {
                lock (item)
                {
                    if (item.CollectionTicket == default)
                    {
                        return false;
                    }

                    item.CollectionTicket = default;
                }

                return Items.TryRemove(item, out _);
            }

            public List<ICollectibleGrainContext> CancelAll()
            {
                List<ICollectibleGrainContext> result = null;
                foreach (var pair in Items)
                {
                    // Attempt to cancel the item. if we succeed, it wasn't already cancelled and we can return it. otherwise, we silently ignore it.
                    var item = pair.Value;
                    lock (item)
                    {
                        if (item.CollectionTicket == default)
                        {
                            continue;
                        }

                        item.CollectionTicket = default;
                    }

                    result ??= new List<ICollectibleGrainContext>();
                    result.Add(pair.Value);
                }

                return result ?? nothing;
            }
        }
    }
}
