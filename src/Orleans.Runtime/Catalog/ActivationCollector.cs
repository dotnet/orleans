using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.Internal;

namespace Orleans.Runtime
{
    /// <summary>
    /// Identifies activations that have been idle long enough to be deactivated.
    /// </summary>
    internal class ActivationCollector : IActivationWorkingSetObserver, ILifecycleParticipant<ISiloLifecycle>, IDisposable
    {
        private readonly TimeSpan quantum;
        private readonly TimeSpan shortestAgeLimit;
        private readonly ConcurrentDictionary<DateTime, Bucket> buckets = new();
        private readonly CancellationTokenSource _shutdownCts = new();
        private DateTime nextTicket;
        private static readonly List<ICollectibleGrainContext> nothing = new(0);
        private readonly ILogger logger;
        private readonly PeriodicTimer _collectionTimer;
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
            _collectionTimer = new PeriodicTimer(quantum);
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
        public Task CollectActivations(TimeSpan ageLimit, CancellationToken cancellationToken) => CollectActivationsImpl(false, ageLimit, cancellationToken);

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
                            // This is not an error scenario because the activation may have become invalid between the time
                            // we captured a snapshot in 'DequeueQuantum' and now. We are not be able to observe such changes.
                            // Do nothing: don't collect, don't reschedule.
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
                            condemned ??= [];
                            condemned.Add(activation);
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
                                    condemned ??= [];
                                    condemned.Add(activation);
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
            if (member is ICollectibleGrainContext activation)
            {
                Interlocked.Increment(ref _activationCount);
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
            if (member is ICollectibleGrainContext activation && TryCancelCollection(activation))
            {
                Interlocked.Decrement(ref _activationCount);
            }
        }

        private Task Start(CancellationToken cancellationToken)
        {
            using var _ = new ExecutionContextSuppressor();
            _collectionLoopTask = RunActivationCollectionLoop();
            return Task.CompletedTask;
        }

        private async Task Stop(CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(() => _shutdownCts.Cancel());
            _collectionTimer.Dispose();

            if (_collectionLoopTask is Task task)
            {
                await task.WaitAsync(cancellationToken);
            }
        }

        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
        {
            lifecycle.Subscribe(
                nameof(ActivationCollector),
                ServiceLifecycleStage.RuntimeServices,
                Start,
                Stop);
        }

        private async Task RunActivationCollectionLoop()
        {
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            var cancellationToken = _shutdownCts.Token;
            while (await _collectionTimer.WaitForNextTickAsync())
            {
                try
                {
                    await this.CollectActivationsImpl(true, ageLimit: default, cancellationToken);
                }
                catch (Exception exception)
                {
                    this.logger.LogError(exception, "Error while collecting activations.");
                }
            }
        }

        private async Task CollectActivationsImpl(bool scanStale, TimeSpan ageLimit, CancellationToken cancellationToken)
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
            if (list is { Count: > 0 })
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("CollectActivations {Activations}", list.ToStrings(d => d.GrainId.ToString() + d.ActivationId));
                await DeactivateActivationsFromCollector(list, cancellationToken);
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
                    list?.Count ?? 0,
                    ToString(),
                    watch.Elapsed);
            }
        }

        private async Task DeactivateActivationsFromCollector(List<ICollectibleGrainContext> list, CancellationToken cancellationToken)
        {
            logger.LogInformation((int)ErrorCode.Catalog_ShutdownActivations_1, "Deactivating '{Count}' idle activations.", list.Count);
            CatalogInstruments.ActivationShutdownViaCollection();

            var reason = GetDeactivationReason();

            var options = new ParallelOptions
            {
                // Avoid passing the cancellation token, since we want all of these activations to be deactivated, even if cancellation is triggered.
                CancellationToken = CancellationToken.None,
                MaxDegreeOfParallelism = Environment.ProcessorCount * 512
            };

            await Parallel.ForEachAsync(list, options, async (activationData, token) =>
            {
                // Continue deactivation when ready.
                activationData.Deactivate(reason, cancellationToken);
                await activationData.Deactivated.ConfigureAwait(false);
            }).WaitAsync(cancellationToken);
        }

        public void Dispose()
        {
            _collectionTimer.Dispose();
            _shutdownCts.Dispose();
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

                    result ??= [];
                    result.Add(pair.Value);
                }

                return result ?? nothing;
            }
        }
    }
}
