using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.Diagnostics;
using Orleans.Runtime.Internal;
using Orleans.Statistics;

namespace Orleans.Runtime
{
    /// <summary>
    /// Identifies activations that have been idle long enough to be deactivated.
    /// </summary>
    internal partial class ActivationCollector : IActivationWorkingSetObserver, ILifecycleParticipant<ISiloLifecycle>, IDisposable
    {
#if NET10_0_OR_GREATER
        private readonly Lock _scheduleLock = new();
#else
        private readonly object _scheduleLock = new();
#endif
        private readonly TimeSpan shortestAgeLimit;
        private readonly ConcurrentDictionary<DateTime, Bucket> buckets = new();
        private readonly ConcurrentDictionary<ICollectibleGrainContext, CollectionRegistration> _registrations = new(ReferenceEqualsComparer.Default);
        private readonly CancellationTokenSource _shutdownCts = new();
        private readonly TimeProvider _timeProvider;
        private DateTime nextTicket;
        private static readonly List<ICollectibleGrainContext> nothing = [];
        private static readonly IReadOnlyList<CollectionClaim> NoClaims = Array.Empty<CollectionClaim>();
        private readonly ILogger logger;
        private int collectionNumber;

        // internal for testing
        internal int _activationCount;

        private readonly PeriodicTimer _collectionTimer;
        private Task? _collectionLoopTask;

        private readonly IEnvironmentStatisticsProvider _environmentStatisticsProvider;
        private readonly GrainCollectionOptions _grainCollectionOptions;
        private readonly CatalogInstruments _catalogInstruments;
        private readonly PeriodicTimer? _memBasedDeactivationTimer;
        private Task? _memBasedDeactivationLoopTask;

        /// <summary>
        /// Initializes a new instance of the <see cref="ActivationCollector"/> class.
        /// </summary>
        /// <param name="timeProvider">The time provider.</param>
        /// <param name="options">The activation collection options.</param>
        /// <param name="logger">The logger.</param>
        /// <param name="environmentStatisticsProvider">The provider used to monitor memory pressure.</param>
        /// <param name="catalogInstruments">The catalog telemetry instruments.</param>
        public ActivationCollector(
            [FromKeyedServices(TimeProviderNames.ActivationManagement)] TimeProvider timeProvider,
            IOptions<GrainCollectionOptions> options,
            ILogger<ActivationCollector> logger,
            IEnvironmentStatisticsProvider environmentStatisticsProvider,
            CatalogInstruments catalogInstruments)
        {
            _timeProvider = timeProvider;
            _grainCollectionOptions = options.Value;
            _catalogInstruments = catalogInstruments;

            shortestAgeLimit = new(_grainCollectionOptions.ClassSpecificCollectionAge.Values.Aggregate(_grainCollectionOptions.CollectionAge.Ticks, (a, v) => Math.Min(a, v.Ticks)));
            nextTicket = MakeTicketFromDateTime(timeProvider.GetUtcNow().UtcDateTime);
            this.logger = logger;
            _collectionTimer = new PeriodicTimer(_grainCollectionOptions.CollectionQuantum);

            _environmentStatisticsProvider = environmentStatisticsProvider;
            if (_grainCollectionOptions.EnableActivationSheddingOnMemoryPressure)
            {
                _memBasedDeactivationTimer = new PeriodicTimer(_grainCollectionOptions.MemoryUsagePollingPeriod);
            }
        }

        // Return the number of activations that were used (touched) in the last recencyPeriod.
        public int GetNumRecentlyUsed(TimeSpan recencyPeriod)
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;
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
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        public Task CollectActivations(TimeSpan ageLimit, CancellationToken cancellationToken) => CollectActivationsImpl(false, ageLimit, cancellationToken);

        internal Task CollectStaleActivations(CancellationToken cancellationToken) => CollectActivationsImpl(scanStale: true, ageLimit: default, cancellationToken: cancellationToken);

        /// <summary>
        /// Schedules the provided grain context for collection if it becomes idle for the specified duration.
        /// </summary>
        /// <param name="item">
        /// The grain context.
        /// </param>
        /// <param name="timeout">
        /// The current idle collection time for the grain.
        /// </param>
        /// <param name="now">The current time, used to calculate when the grain becomes eligible for collection.</param>
        public void ScheduleCollection(ICollectibleGrainContext item, TimeSpan timeout, DateTime now)
        {
            if (item.IsExemptFromCollection)
            {
                return;
            }

            var registration = _registrations.GetOrAdd(item, static item => new(item));
            lock (_scheduleLock)
            {
                var ticket = MakeTicketFromTimeSpan(timeout, now);
                if (!registration.TrySchedule(GetOrCreateBucket(ticket), ticket))
                {
                    throw new InvalidOperationException("Call CancelCollection before calling ScheduleCollection.");
                }
            }
        }

        /// <summary>
        /// Tries the cancel idle activation collection.
        /// </summary>
        /// <param name="item">The grain context.</param>
        /// <returns><see langword="true"/> if collection was canceled, <see langword="false"/> otherwise.</returns>
        public bool TryCancelCollection(ICollectibleGrainContext? item)
        {
            if (item is null) return false;
            if (item.IsExemptFromCollection) return false;

            return _registrations.TryGetValue(item, out var registration) && registration.TryCancel();
        }

        /// <summary>
        /// Tries the reschedule collection.
        /// </summary>
        /// <param name="item">The grain context.</param>
        /// <returns><see langword="true"/> if collection was canceled, <see langword="false"/> otherwise.</returns>
        public bool TryRescheduleCollection(ICollectibleGrainContext item)
        {
            if (item.IsExemptFromCollection) return false;
            var registration = _registrations.GetOrAdd(item, static item => new(item));

            lock (_scheduleLock)
            {
                return TryRescheduleCollection(registration, item.CollectionAgeLimit);
            }
        }

        private bool TryRescheduleCollection(CollectionRegistration registration, TimeSpan timeout)
        {
            if (registration.TryGetScheduledTicket(out var oldTicket))
            {
                ThrowIfTicketIsInvalid(oldTicket);
                if (!IsExpired(oldTicket))
                {
                    var rescheduledTicket = MakeTicketFromTimeSpan(timeout, _timeProvider.GetUtcNow().UtcDateTime);
                    if (rescheduledTicket.Equals(oldTicket)) return true;

                    return registration.TryReschedule(GetOrCreateBucket(rescheduledTicket), rescheduledTicket);
                }
            }

            registration.TryCancel();
            var newTicket = MakeTicketFromTimeSpan(timeout, _timeProvider.GetUtcNow().UtcDateTime);
            return registration.TrySchedule(GetOrCreateBucket(newTicket), newTicket);
        }

        private bool DequeueQuantum([NotNullWhen(true)] out IReadOnlyList<CollectionClaim>? items, DateTime now)
        {
            Bucket? bucket;
            lock (_scheduleLock)
            {
                if (nextTicket > now)
                {
                    items = null;
                    return false;
                }

                var key = nextTicket;
                nextTicket += _grainCollectionOptions.CollectionQuantum;
                buckets.TryRemove(key, out bucket);
            }

            if (bucket is null)
            {
                items = NoClaims;
                return true;
            }

            items = bucket.ClaimAll();
            return true;
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var all = buckets.ToList();
            var bucketsText = Utils.EnumerableToString(all.OrderBy(bucket => bucket.Key), bucket => $"{Utils.TimeSpanToString(bucket.Key - now)}->{bucket.Value.Items.Count} items");
            return $"<#Activations={all.Sum(b => b.Value.Items.Count)}, #Buckets={all.Count}, buckets={bucketsText}>";
        }

        private List<ICollectibleGrainContext> ScanStale(DeactivationReason reason, CancellationToken cancellationToken)
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            List<ICollectibleGrainContext>? condemned = null;
            while (DequeueQuantum(out var claims, now))
            {
                foreach (var claim in claims)
                {
                    var activation = claim.Registration.Context;
                    var result = activation.TryDeactivateForCollection(
                        reason,
                        now,
                        activation.CollectionAgeLimit,
                        respectKeepAlive: true,
                        cancellationToken);
                    switch (result.Action)
                    {
                        case ActivationCollectionAction.StartedDeactivation:
                            claim.Registration.TryCompleteClaim(claim.Generation);
                            condemned ??= [];
                            condemned.Add(activation);
                            break;
                        case ActivationCollectionAction.Reschedule:
                            RescheduleClaim(claim, result.RescheduleAfter, now);
                            break;
                        default:
                            claim.Registration.TryCompleteClaim(claim.Generation);
                            break;
                    }
                }
            }

            return condemned ?? nothing;
        }

        private List<ICollectibleGrainContext> ScanAll(
            TimeSpan ageLimit,
            DeactivationReason reason,
            CancellationToken cancellationToken)
        {
            List<ICollectibleGrainContext>? condemned = null;
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            foreach (var kv in buckets)
            {
                var bucket = kv.Value;
                foreach (var registration in bucket.Items.Keys)
                {
                    if (!registration.IsScheduledIn(bucket))
                    {
                        continue;
                    }

                    var activation = registration.Context;
                    var result = activation.TryDeactivateForCollection(
                        reason,
                        now,
                        ageLimit,
                        respectKeepAlive: true,
                        cancellationToken);
                    if (result.Action is ActivationCollectionAction.StartedDeactivation)
                    {
                        registration.TryCancel();
                        condemned ??= [];
                        condemned.Add(activation);
                    }
                    else if (result.Action is ActivationCollectionAction.Remove)
                    {
                        registration.TryCancel();
                    }
                }
            }

            return condemned ?? nothing;
        }

        private void RescheduleClaim(CollectionClaim claim, TimeSpan timeout, DateTime now)
        {
            lock (_scheduleLock)
            {
                var ticket = MakeTicketFromTimeSpan(timeout, now);
                claim.Registration.TryRescheduleClaim(claim.Generation, GetOrCreateBucket(ticket), ticket);
            }
        }

        // Internal for testing. It's expected that when this returns true, activation shedding will occur.
        internal bool IsMemoryOverloaded(out int surplusActivationCount)
        {
            var activationCount = _activationCount;
            if (activationCount == 0)
            {
                surplusActivationCount = 0;
                return false;
            }

            var stats = _environmentStatisticsProvider.GetEnvironmentStatistics();
            var limit = _grainCollectionOptions.MemoryUsageLimitPercentage / 100f;

            var usage = stats.NormalizedMemoryUsage;
            if (usage <= limit)
            {
                // High memory pressure is not detected, so we do not need to deactivate any activations.
                surplusActivationCount = 0;
                return false;
            }

            // Calculate the surplus activations based the memory usage target.
            var target = _grainCollectionOptions.MemoryUsageTargetPercentage / 100f;
            surplusActivationCount = (int)Math.Max(0, activationCount - Math.Floor(activationCount * target / usage));
            if (surplusActivationCount <= 0)
            {
                surplusActivationCount = 0;
                return false;
            }

            var surplusActivationPercentage = 100 * (1 - target / usage);
            LogCurrentHighMemoryPressureStats(stats.MemoryUsagePercentage, _grainCollectionOptions.MemoryUsageLimitPercentage, deactivationTarget: surplusActivationCount, activationCount, surplusActivationPercentage);
            return true;
        }

        /// <summary>
        /// Deactivates up to <paramref name="count"/> activations in due-time order.
        /// </summary>
        /// <param name="count">The number of activations to deactivate.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task representing the deactivation operation.</returns>
        /// <remarks>Internal for testing.</remarks>
        internal async Task DeactivateInDueTimeOrder(int count, CancellationToken cancellationToken)
        {
            var watch = ValueStopwatch.StartNew();
            var number = Interlocked.Increment(ref collectionNumber);
            long memBefore = GC.GetTotalMemory(false) / (1024 * 1024); // MB
            LogBeforeCollection(number, memBefore, _activationCount, this);

            var candidates = new List<ICollectibleGrainContext>(count);
            var reason = new DeactivationReason(
                DeactivationReasonCode.HighMemoryPressure,
                $"Process memory utilization exceeded the configured limit of '{_grainCollectionOptions.MemoryUsageLimitPercentage}'. Detected memory usage is {memBefore} MB.");

            // snapshot to avoid concurrency collection modification issues
            var bucketSnapshot = buckets.ToArray();
            Array.Sort(bucketSnapshot, static (left, right) => left.Key.CompareTo(right.Key));
            foreach (var bucket in bucketSnapshot)
            {
                foreach (var registration in bucket.Value.Items.Keys)
                {
                    if (candidates.Count >= count)
                    {
                        break;
                    }

                    if (!registration.IsScheduledIn(bucket.Value))
                    {
                        continue;
                    }

                    var activation = registration.Context;
                    var result = activation.TryDeactivateForCollection(
                        reason,
                        _timeProvider.GetUtcNow().UtcDateTime,
                        ageLimit: TimeSpan.Zero,
                        respectKeepAlive: false,
                        cancellationToken);
                    if (result.Action is ActivationCollectionAction.StartedDeactivation)
                    {
                        registration.TryCancel();
                        candidates.Add(activation);
                    }
                    else if (result.Action is ActivationCollectionAction.Remove)
                    {
                        registration.TryCancel();
                    }
                }

                if (candidates.Count >= count)
                {
                    break;
                }
            }

            _catalogInstruments.OnActivationCollected();
            if (candidates.Count > 0)
            {
                LogCollectActivations(new(candidates));

                await AwaitDeactivatedActivationsFromCollector(
                    candidates,
                    cancellationToken,
                    reason,
                    ActivationCollectionEvents.CollectionSource.MemoryPressure,
                    ageLimit: default);
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
            // DateTime.MaxValue is a sentinel produced by MakeTicketFromDateTime when the
            // rounded-up tick overflows (e.g., ScanStale rescheduling an activation whose
            // KeepAliveUntil is DateTime.MaxValue). Its ticks aren't quantum-aligned, but
            // it is a valid ticket and must not be rejected here.
            if (ticket == DateTime.MaxValue) return;
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

        private Bucket GetOrCreateBucket(DateTime ticket) => buckets.GetOrAdd(ticket, static _ => new());

        internal DateTime GetCollectionTicketForTesting(ICollectibleGrainContext item)
            => _registrations.TryGetValue(item, out var registration) ? registration.Ticket : default;

        private void EnsureCollectionScheduled(ICollectibleGrainContext item)
        {
            if (item.IsExemptFromCollection)
            {
                return;
            }

            var registration = _registrations.GetOrAdd(item, static item => new(item));
            if (registration.IsTracked)
            {
                return;
            }

            lock (_scheduleLock)
            {
                if (!registration.IsTracked)
                {
                    var ticket = MakeTicketFromTimeSpan(item.CollectionAgeLimit, _timeProvider.GetUtcNow().UtcDateTime);
                    registration.TrySchedule(GetOrCreateBucket(ticket), ticket);
                }
            }
        }

        void IActivationWorkingSetObserver.OnAdded(IActivationWorkingSetMember member)
        {
            if (member is ICollectibleGrainContext activation)
            {
                Interlocked.Increment(ref _activationCount);
                ScheduleCollection(activation, activation.CollectionAgeLimit, _timeProvider.GetUtcNow().UtcDateTime);
            }
        }

        void IActivationWorkingSetObserver.OnActive(IActivationWorkingSetMember member)
        {
            // We do not need to do anything when a grain becomes active, since we can lazily handle it when scanning its bucket instead.
            // This reduces the amount of unnecessary work performed.
        }

        void IActivationWorkingSetObserver.OnEvicted(IActivationWorkingSetMember member)
        {
            if (member is ICollectibleGrainContext activation)
            {
                EnsureCollectionScheduled(activation);
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
                _ = TryCancelCollection(activation);
                _registrations.TryRemove(activation, out _);
            }
        }

        private Task Start(CancellationToken cancellationToken)
        {
            using var _ = new ExecutionContextSuppressor();
            _collectionLoopTask = RunActivationCollectionLoop();

            if (_grainCollectionOptions.EnableActivationSheddingOnMemoryPressure)
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

            try
            {
                while (await _memBasedDeactivationTimer!.WaitForNextTickAsync(cancellationToken))
                {
                    try
                    {
                        var currentGen2GcCount = GC.CollectionCount(2);

                        // note: GC.CollectionCount(2) will return 0 if no gen2 gc happened yet and we rely on this behavior:
                        //       high memory pressure situation cannot occur until gen2 occurred at least once
                        if (currentGen2GcCount <= lastGen2GcCount)
                        {
                            // No Gen2 GC since last deactivation cycle.
                            // Wait for Gen2 GC between cycles to be sure that 
                            continue;
                        }

                        if (!IsMemoryOverloaded(out var surplusActivationCount))
                        {
                            continue;
                        }

                        lastGen2GcCount = currentGen2GcCount;
                        await DeactivateInDueTimeOrder(surplusActivationCount, cancellationToken);
                    }
                    catch (Exception exception)
                    {
                        // Ignore cancellation exceptions during shutdown.
                        if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }

                        LogErrorWhileCollectingActivations(exception);
                    }
                }
            }
            catch (Exception exception)
            {
                if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
                {
                    // Ignore cancellation exceptions during shutdown.
                }
                else
                {
                    throw;
                }
            }
        }

        private async Task CollectActivationsImpl(bool scanStale, TimeSpan ageLimit, CancellationToken cancellationToken)
        {
            var watch = ValueStopwatch.StartNew();
            var number = Interlocked.Increment(ref collectionNumber);
            long memBefore = GC.GetTotalMemory(false) / (1024 * 1024);

            LogBeforeCollection(number, memBefore, _activationCount, this);

            var deactivationReason = GetDeactivationReason();
            List<ICollectibleGrainContext> list = scanStale
                ? ScanStale(deactivationReason, cancellationToken)
                : ScanAll(ageLimit, deactivationReason, cancellationToken);
            _catalogInstruments.OnActivationCollected();
            if (list is { Count: > 0 })
            {
                LogCollectActivations(new(list));
                await AwaitDeactivatedActivationsFromCollector(
                    list,
                    cancellationToken,
                    deactivationReason,
                    collectionSource: scanStale ? ActivationCollectionEvents.CollectionSource.Stale : ActivationCollectionEvents.CollectionSource.AgeLimit,
                    ageLimit: ageLimit);
            }

            long memAfter = GC.GetTotalMemory(false) / (1024 * 1024);
            watch.Stop();

            LogAfterCollection(number, memAfter, _activationCount, list?.Count ?? 0, this, watch.Elapsed);
        }

        private async Task AwaitDeactivatedActivationsFromCollector(
            List<ICollectibleGrainContext> list,
            CancellationToken cancellationToken,
            DeactivationReason deactivationReason,
            ActivationCollectionEvents.CollectionSource collectionSource,
            TimeSpan ageLimit)
        {
            LogDeactivateActivationsFromCollector(list.Count);
            _catalogInstruments.ActivationShutdownViaCollection();

            var options = new ParallelOptions
            {
                // Avoid passing the cancellation token, since we want all of these activations to be deactivated, even if cancellation is triggered.
                CancellationToken = CancellationToken.None,
                MaxDegreeOfParallelism = Environment.ProcessorCount * 512
            };

            await Parallel.ForEachAsync(list, options, async (activationData, token) =>
            {
                await activationData.Deactivated.ConfigureAwait(false);
            }).WaitAsync(cancellationToken);

            ActivationCollectionEvents.EmitCollectionCompleted(collectionSource, ageLimit, deactivationReason, list);
        }

        public void Dispose()
        {
            _collectionTimer.Dispose();
            _shutdownCts.Dispose();
            _memBasedDeactivationTimer?.Dispose();
        }

        private readonly record struct CollectionClaim(CollectionRegistration Registration, long Generation);

        private enum CollectionRegistrationState
        {
            None,
            Scheduled,
            Claimed
        }

        private sealed class CollectionRegistration(ICollectibleGrainContext context)
        {
#if NET10_0_OR_GREATER
            private readonly Lock _lock = new();
#else
            private readonly object _lock = new();
#endif
            private Bucket? _bucket;
            private long _generation;
            private CollectionRegistrationState _state;
            private DateTime _ticket;

            public ICollectibleGrainContext Context { get; } = context;

            public bool IsTracked
            {
                get
                {
                    lock (_lock)
                    {
                        return _state is not CollectionRegistrationState.None;
                    }
                }
            }

            public DateTime Ticket
            {
                get
                {
                    lock (_lock)
                    {
                        return _state is CollectionRegistrationState.Scheduled ? _ticket : default;
                    }
                }
            }

            public bool TrySchedule(Bucket bucket, DateTime ticket)
            {
                lock (_lock)
                {
                    if (_state is not CollectionRegistrationState.None)
                    {
                        return false;
                    }

                    if (!bucket.Items.TryAdd(this, 0))
                    {
                        throw new InvalidOperationException("Collection registration is already associated with this bucket.");
                    }

                    _bucket = bucket;
                    _ticket = ticket;
                    _state = CollectionRegistrationState.Scheduled;
                    _generation++;
                    return true;
                }
            }

            public bool TryGetScheduledTicket(out DateTime ticket)
            {
                lock (_lock)
                {
                    if (_state is CollectionRegistrationState.Scheduled)
                    {
                        ticket = _ticket;
                        return true;
                    }

                    ticket = default;
                    return false;
                }
            }

            public bool TryReschedule(Bucket bucket, DateTime ticket)
            {
                lock (_lock)
                {
                    if (_state is not CollectionRegistrationState.Scheduled)
                    {
                        return false;
                    }

                    if (ReferenceEquals(_bucket, bucket))
                    {
                        _ticket = ticket;
                        return true;
                    }

                    _bucket!.Items.TryRemove(this, out _);
                    if (!bucket.Items.TryAdd(this, 0))
                    {
                        throw new InvalidOperationException("Collection registration is already associated with the destination bucket.");
                    }

                    _bucket = bucket;
                    _ticket = ticket;
                    _generation++;
                    return true;
                }
            }

            public bool TryCancel()
            {
                lock (_lock)
                {
                    if (_state is CollectionRegistrationState.None)
                    {
                        return false;
                    }

                    _bucket?.Items.TryRemove(this, out _);
                    _bucket = null;
                    _ticket = default;
                    _state = CollectionRegistrationState.None;
                    _generation++;
                    return true;
                }
            }

            public bool IsScheduledIn(Bucket bucket)
            {
                lock (_lock)
                {
                    return _state is CollectionRegistrationState.Scheduled && ReferenceEquals(_bucket, bucket);
                }
            }

            public bool TryClaim(Bucket bucket, out CollectionClaim claim)
            {
                lock (_lock)
                {
                    if (_state is not CollectionRegistrationState.Scheduled || !ReferenceEquals(_bucket, bucket))
                    {
                        claim = default;
                        return false;
                    }

                    _bucket = null;
                    _ticket = default;
                    _state = CollectionRegistrationState.Claimed;
                    var generation = ++_generation;
                    claim = new CollectionClaim(this, generation);
                    return true;
                }
            }

            public bool TryCompleteClaim(long generation)
            {
                lock (_lock)
                {
                    if (_state is not CollectionRegistrationState.Claimed || _generation != generation)
                    {
                        return false;
                    }

                    _state = CollectionRegistrationState.None;
                    _generation++;
                    return true;
                }
            }

            public bool TryRescheduleClaim(long generation, Bucket bucket, DateTime ticket)
            {
                lock (_lock)
                {
                    if (_state is not CollectionRegistrationState.Claimed || _generation != generation)
                    {
                        return false;
                    }

                    if (!bucket.Items.TryAdd(this, 0))
                    {
                        throw new InvalidOperationException("Collection registration is already associated with the destination bucket.");
                    }

                    _bucket = bucket;
                    _ticket = ticket;
                    _state = CollectionRegistrationState.Scheduled;
                    _generation++;
                    return true;
                }
            }
        }

        private sealed class Bucket
        {
            public ConcurrentDictionary<CollectionRegistration, byte> Items { get; } = new(ReferenceEqualsComparer.Default);

            public IReadOnlyList<CollectionClaim> ClaimAll()
            {
                List<CollectionClaim>? result = null;
                foreach (var registration in Items.Keys)
                {
                    if (registration.TryClaim(this, out var claim))
                    {
                        result ??= [];
                        result.Add(claim);
                    }
                }

                return result ?? NoClaims;
            }
        }

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "High memory pressure detected ({MemoryUsagePercentage:F2}% > {MemoryUsageLimitPercentage:F2}%). Deactivating up to {DeactivationTarget:N0}/{ActivationCount:N0} ({SurplusActivationPercentage:F2}%) grains to free memory."
        )]
        private partial void LogCurrentHighMemoryPressureStats(double memoryUsagePercentage, double memoryUsageLimitPercentage, int deactivationTarget, int activationCount, double surplusActivationPercentage);

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
            Message = "After collection #{CollectionNumber} memory: {MemoryAfter}MB, #activations: {ActivationCount}, collected {CollectedCount} activations, collector: {CollectorStatus}, collection time: {CollectionTime}"
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
