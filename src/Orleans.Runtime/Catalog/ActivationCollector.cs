using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.Internal;
using Orleans.Statistics;

namespace Orleans.Runtime
{
    /// <summary>
    /// Identifies activations that have been idle long enough to be deactivated.
    /// </summary>
    internal partial class ActivationCollector : IActivationWorkingSetObserver, ILifecycleParticipant<ISiloLifecycle>, IDisposable
    {
        private readonly TimeSpan shortestAgeLimit;
        private readonly ConcurrentDictionary<DateTime, Bucket> buckets = new();
        private readonly CancellationTokenSource _shutdownCts = new();
        private DateTime nextTicket;
        private static readonly List<ICollectibleGrainContext> nothing = new(0);
        private readonly ILogger logger;
        private int collectionNumber;

        // internal for testing
        internal int _activationCount;

        private readonly PeriodicTimer _collectionTimer;
        private Task _collectionLoopTask;

        private readonly IEnvironmentStatisticsProvider _environmentStatisticsProvider;
        private readonly GrainCollectionOptions _grainCollectionOptions;
        private readonly PeriodicTimer _memBasedDeactivationTimer;
        private Task _memBasedDeactivationLoopTask;

        /// <summary>
        /// Initializes a new instance of the <see cref="ActivationCollector"/> class.
        /// </summary>
        /// <param name="timeProvider">The time provider.</param>
        /// <param name="options">The options.</param>
        /// <param name="logger">The logger.</param>
        public ActivationCollector(
            TimeProvider timeProvider,
            IOptions<GrainCollectionOptions> options,
            ILogger<ActivationCollector> logger,
            IEnvironmentStatisticsProvider environmentStatisticsProvider)
        {
            _grainCollectionOptions = options.Value;

            shortestAgeLimit = new(_grainCollectionOptions.ClassSpecificCollectionAge.Values.Aggregate(_grainCollectionOptions.CollectionAge.Ticks, (a, v) => Math.Min(a, v.Ticks)));
            nextTicket = MakeTicketFromDateTime(timeProvider.GetUtcNow().UtcDateTime);
            this.logger = logger;
            _collectionTimer = new PeriodicTimer(_grainCollectionOptions.CollectionQuantum);

            _environmentStatisticsProvider = environmentStatisticsProvider;
            if (_grainCollectionOptions.MemoryUsageCollectionEnabled)
            {
                _memBasedDeactivationTimer = new PeriodicTimer(_grainCollectionOptions.MemoryUsagePollingPeriod);
            }
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
            if (item is null) return false;
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
                nextTicket += _grainCollectionOptions.CollectionQuantum;
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

        /// <summary>
        /// Checks if current memory usage is above the configured threshold for deactivation.
        /// Also calculates the target number of activations to keep active if memory is overloaded to reach target memory usage.
        /// </summary>
        /// <remarks>internal for testing</remarks>
        /// <param name="targetActivationLimit">number of activations to keep active to bring back memory consumption to target value</param>
        /// <param name="currentGen2GcCount">current gen2 gc count</param>
        internal bool IsMemoryOverloaded(int currentGen2GcCount, out int targetActivationLimit)
        {
            targetActivationLimit = 0;
            var stats = _environmentStatisticsProvider.GetEnvironmentStatistics();

            // filtering harms here: we need raw and precise statistics to calculate memory usage correctly
            var usedMemory = stats.RawMemoryUsageBytes;
            var memoryCapacity = usedMemory + stats.RawAvailableMemoryBytes;

            Debug.Assert(usedMemory >= 0, "Memory usage cannot be negative.");
            Debug.Assert(memoryCapacity >= 0 && memoryCapacity >= usedMemory, "Memory capacity should be [0: used memory + available memory]");

            var activationCount = _activationCount > 0 ? _activationCount : 1;
            var activationSize = usedMemory / (double)activationCount;

            var threshold = _grainCollectionOptions.MemoryUsageLimitPercentage;
            var targetThreshold = _grainCollectionOptions.MemoryUsageTargetPercentage;
            var memoryLoadPercentage = 100.0 * usedMemory / memoryCapacity;
            if (memoryLoadPercentage < threshold)
            {
                return false;
            }

            var targetUsedMemory = targetThreshold * memoryCapacity / 100.0;
            targetActivationLimit = (int)Math.Floor(targetUsedMemory / activationSize);
            if (targetActivationLimit < 0)
            {
                targetActivationLimit = 0;
            }

            LogCurrentHighMemoryPressureStats(currentGen2GcCount: currentGen2GcCount, stats);
            return true;
        }

        /// <summary>
        /// Deactivates <param name="count" /> activations in due time order
        /// <remarks>internal for testing</remarks>
        /// </summary>
        internal async Task DeactivateInDueTimeOrder(int count, CancellationToken cancellationToken)
        {
            LogHighMemoryPressureDeactivationStarted(count);

            var watch = ValueStopwatch.StartNew();
            var number = Interlocked.Increment(ref collectionNumber);
            long memBefore = GC.GetTotalMemory(false) / (1024 * 1024); // MB
            LogBeforeCollection(number, memBefore, _activationCount, this);

            var candidates = new List<ICollectibleGrainContext>(count);

            foreach (var bucket in buckets.OrderBy(b => b.Key))
            {
                foreach (var item in bucket.Value.Items)
                {
                    if (candidates.Count >= count)
                    {
                        break;
                    }

                    var activation = item.Value;
                    lock (activation)
                    {
                        if (!activation.IsValid || !activation.IsInactive)
                        {
                            continue;
                        }
                    }

                    candidates.Add(activation);
                }

                if (candidates.Count >= count)
                {
                    break;
                }
            }

            CatalogInstruments.ActivationCollections.Add(1);
            if (candidates.Count > 0) 
            {
                LogCollectActivations(new(candidates));

                var reason = new DeactivationReason(
                    DeactivationReasonCode.HighMemoryPressure,
                    $"Process memory utilization exceeded the configured limit of '{_grainCollectionOptions.MemoryUsageLimitPercentage}'. Detected memory usage is {memBefore} MB.");

                await DeactivateActivationsFromCollector(candidates, cancellationToken, reason);
            }

            long memAfter = GC.GetTotalMemory(false) / (1024 * 1024);
            watch.Stop();
            LogAfterCollection(number, memAfter, _activationCount, candidates.Count, this, watch.Elapsed);
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
            if (0 != ticket.Ticks % _grainCollectionOptions.CollectionQuantum.Ticks)
            {
                throw new ArgumentException(string.Format("invalid ticket ({0})", ticket));
            }
        }

        private bool IsExpired(DateTime ticket)
        {
            return ticket < nextTicket;
        }

        public DateTime MakeTicketFromDateTime(DateTime timestamp)
        {
            // Round the timestamp to the next _grainCollectionOptions.CollectionQuantum. e.g. if the _grainCollectionOptions.CollectionQuantum is 1 minute and the timestamp is 3:45:22, then the ticket will be 3:46.
            // Note that TimeStamp.Ticks and DateTime.Ticks both return a long.
            var ticketTicks = ((timestamp.Ticks - 1) / _grainCollectionOptions.CollectionQuantum.Ticks + 1) * _grainCollectionOptions.CollectionQuantum.Ticks;
            if (ticketTicks > DateTime.MaxValue.Ticks)
            {
                return DateTime.MaxValue;
            }

            var ticket = new DateTime(ticketTicks, DateTimeKind.Utc);
            if (ticket < nextTicket)
            {
                throw new ArgumentException(string.Format("The earliest collection that can be scheduled from now is for {0}", new DateTime(nextTicket.Ticks - _grainCollectionOptions.CollectionQuantum.Ticks + 1, DateTimeKind.Utc)));
            }

            return ticket;
        }

        private DateTime MakeTicketFromTimeSpan(TimeSpan timeout, DateTime now)
        {
            if (timeout < _grainCollectionOptions.CollectionQuantum)
            {
                throw new ArgumentException(string.Format("timeout must be at least {0}, but it is {1}", _grainCollectionOptions.CollectionQuantum, timeout), nameof(timeout));
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
            Interlocked.Decrement(ref _activationCount);
            _ = TryCancelCollection(member as ICollectibleGrainContext);
        }

        private Task Start(CancellationToken cancellationToken)
        {
            using var _ = new ExecutionContextSuppressor();
            _collectionLoopTask = RunActivationCollectionLoop();

            if (_grainCollectionOptions.MemoryUsageCollectionEnabled)
            {
                _memBasedDeactivationLoopTask = RunMemoryBasedDeactivationLoop();
            }

            return Task.CompletedTask;
        }

        private async Task Stop(CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(() => _shutdownCts.Cancel());
            _collectionTimer.Dispose();
            _memBasedDeactivationTimer?.Dispose();

            if (_collectionLoopTask is Task task)
            {
                await task.WaitAsync(cancellationToken);
            }

            if (_memBasedDeactivationLoopTask is Task deactivationLoopTask)
            {
                await deactivationLoopTask.WaitAsync(cancellationToken);
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
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // most probably shutdown
                }
                catch (Exception exception)
                {
                    LogErrorWhileCollectingActivations(exception);
                }
            }
        }

        private async Task RunMemoryBasedDeactivationLoop()
        {
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            var cancellationToken = _shutdownCts.Token;

            int lastGen2GcCount = 0;
            int activationLimit = int.MaxValue;
            int newTargetActivationLimit = int.MaxValue;

            while (await _memBasedDeactivationTimer.WaitForNextTickAsync())
            {
                try
                {
                    var currentGen2GcCount = GC.CollectionCount(2);

                    // note: GC.CollectionCount(2) will return 0 if no gen2 gc happened yet and we rely on this behavior:
                    //       high memory pressure situation cannot occur until gen2 occurred at least once
                    if (currentGen2GcCount > lastGen2GcCount && IsMemoryOverloaded(currentGen2GcCount, out newTargetActivationLimit))
                    {
                        // recalculate the surplus activations based on the new target activation limit
                        var surplusActivations = Math.Max(0, _activationCount - newTargetActivationLimit);

                        activationLimit = newTargetActivationLimit;
                        lastGen2GcCount = currentGen2GcCount;
                        await DeactivateInDueTimeOrder(surplusActivations, cancellationToken);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // most probably shutdown
                }
                catch (Exception exception)
                {
                    LogErrorWhileCollectingActivations(exception);
                }
            }
        }

        private async Task CollectActivationsImpl(bool scanStale, TimeSpan ageLimit, CancellationToken cancellationToken)
        {
            var watch = ValueStopwatch.StartNew();
            var number = Interlocked.Increment(ref collectionNumber);
            long memBefore = GC.GetTotalMemory(false) / (1024 * 1024);

            LogBeforeCollection(number, memBefore, _activationCount, this);

            List<ICollectibleGrainContext> list = scanStale ? ScanStale() : ScanAll(ageLimit);
            CatalogInstruments.ActivationCollections.Add(1);
            if (list is { Count: > 0 })
            {
                LogCollectActivations(new(list));
                await DeactivateActivationsFromCollector(list, cancellationToken);
            }

            long memAfter = GC.GetTotalMemory(false) / (1024 * 1024);
            watch.Stop();

            LogAfterCollection(number, memAfter, _activationCount, list?.Count ?? 0, this, watch.Elapsed);
        }

        private async Task DeactivateActivationsFromCollector(List<ICollectibleGrainContext> list, CancellationToken cancellationToken, DeactivationReason? deactivationReason = null)
        {
            LogDeactivateActivationsFromCollector(list.Count);
            CatalogInstruments.ActivationShutdownViaCollection();

            deactivationReason ??= GetDeactivationReason();

            var options = new ParallelOptions
            {
                // Avoid passing the cancellation token, since we want all of these activations to be deactivated, even if cancellation is triggered.
                CancellationToken = CancellationToken.None,
                MaxDegreeOfParallelism = Environment.ProcessorCount * 512
            };

            await Parallel.ForEachAsync(list, options, async (activationData, token) =>
            {
                // Continue deactivation when ready.
                activationData.Deactivate(deactivationReason.Value, cancellationToken);
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

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "High memory pressure detected. Starting {count} deactivations."
        )]
        private partial void LogHighMemoryPressureDeactivationStarted(int count);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "High memory pressure: forced deactivations and waiting for GC2 (gen2 count: {CurrentGen2GcCount}) collection. {Statistics}"
        )]
        private partial void LogCurrentHighMemoryPressureStats(int currentGen2GcCount, EnvironmentStatistics statistics);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Error while collecting activations."
        )]
        private partial void LogErrorWhileCollectingActivations(Exception exception);

        [LoggerMessage(
            EventId = (int)ErrorCode.Catalog_BeforeCollection,
            Level = LogLevel.Debug,
            Message = "Before collection #{CollectionNumber}: memory: {MemoryBefore}MB, #activations: {ActivationCount}, collector: {CollectorStatus}"
        )]
        private partial void LogBeforeCollection(int collectionNumber, long memoryBefore, int activationCount, ActivationCollector collectorStatus);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "CollectActivations {Activations}"
        )]
        private partial void LogCollectActivations(ActivationsLogValue activations);
        private struct ActivationsLogValue(List<ICollectibleGrainContext> list)
        {
            public override string ToString() => list.ToStrings(d => d.GrainId.ToString() + d.ActivationId);
        }

        [LoggerMessage(
            EventId = (int)ErrorCode.Catalog_AfterCollection,
            Level = LogLevel.Debug,
            Message =  "After collection #{CollectionNumber} memory: {MemoryAfter}MB, #activations: {ActivationCount}, collected {CollectedCount} activations, collector: {CollectorStatus}, collection time: {CollectionTime}"
        )]
        private partial void LogAfterCollection(int collectionNumber, long memoryAfter, int activationCount, int collectedCount, ActivationCollector collectorStatus, TimeSpan collectionTime);

        [LoggerMessage(
            EventId = (int)ErrorCode.Catalog_ShutdownActivations_1,
            Level = LogLevel.Information,
            Message = "Deactivating '{Count}' idle activations."
        )]
        private partial void LogDeactivateActivationsFromCollector(int count);
    }
}
