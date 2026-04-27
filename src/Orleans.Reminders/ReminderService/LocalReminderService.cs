using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.CodeGeneration;
using Orleans.GrainReferences;
using Orleans.Internal;
using Orleans.Metadata;
using Orleans.Reminders.Diagnostics;
using Orleans.Runtime.ConsistentRing;
using Orleans.Runtime.Internal;
using Orleans.Runtime.Scheduler;

namespace Orleans.Runtime.ReminderService
{
    internal sealed partial class LocalReminderService : GrainService, IReminderService, ILifecycleParticipant<ISiloLifecycle>
    {
        private const int InitialReadRetryCountBeforeFastFailForUpdates = 2;
        private static readonly TimeSpan InitialReadMaxWaitTimeForUpdates = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan InitialReadRetryPeriod = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan MinimumReminderDueTime = TimeSpan.FromMilliseconds(1);
        private readonly ILogger logger;
        private readonly ReminderOptions reminderOptions;
        private readonly Dictionary<ReminderIdentity, LocalReminderData> localReminders = new();
        private readonly IReminderTable reminderTable;
        private readonly TaskCompletionSource<bool> startedTask;
        private readonly IAsyncTimerFactory asyncTimerFactory;
        private readonly IAsyncTimer listRefreshTimer; // timer that refreshes our list of reminders to reflect global reminder table
        private readonly GrainReferenceActivator _referenceActivator;
        private readonly GrainInterfaceType _grainInterfaceType;
        private readonly TimeProvider _timeProvider;
        private long localTableSequence;
        private uint initialReadCallCount = 0;
        private Task? runTask;

        public LocalReminderService(
            GrainReferenceActivator referenceActivator,
            GrainInterfaceTypeResolver interfaceTypeResolver,
            IReminderTable reminderTable,
            IAsyncTimerFactory asyncTimerFactory,
            IOptions<ReminderOptions> reminderOptions,
            IConsistentRingProvider ringProvider,
            TimeProvider timeProvider,
            SystemTargetShared shared)
            : base(
                  SystemTargetGrainId.CreateGrainServiceGrainId(GrainInterfaceUtils.GetGrainClassTypeCode(typeof(IReminderService)), null!, shared.SiloAddress),
                  ringProvider,
                  shared)
        {
            _referenceActivator = referenceActivator;
            _grainInterfaceType = interfaceTypeResolver.GetGrainInterfaceType(typeof(IRemindable));
            this.reminderOptions = reminderOptions.Value;
            this.reminderTable = reminderTable;
            this.asyncTimerFactory = asyncTimerFactory;
            _timeProvider = timeProvider;
            ReminderInstruments.RegisterActiveRemindersObserve(() => localReminders.Count);
            startedTask = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            this.logger = shared.LoggerFactory.CreateLogger<LocalReminderService>();
            this.listRefreshTimer = asyncTimerFactory.Create(this.reminderOptions.RefreshReminderListPeriod, "ReminderService.ReminderListRefresher");
            shared.ActivationDirectory.RecordNewTarget(this);
        }

        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle observer)
        {
            observer.Subscribe(
                nameof(LocalReminderService),
                ServiceLifecycleStage.BecomeActive,
                async ct =>
                {
                    try
                    {
                        await this.QueueTask(() => Initialize(ct));
                    }
                    catch (Exception exception)
                    {
                        LogErrorActivatingReminderService(exception);
                        throw;
                    }
                },
                async ct =>
                {
                    try
                    {
                        await this.QueueTask(Stop).WaitAsync(ct);
                    }
                    catch (Exception exception)
                    {
                        LogErrorStoppingReminderService(exception);
                        throw;
                    }
                });
            observer.Subscribe(
                nameof(LocalReminderService),
                ServiceLifecycleStage.Active,
                async ct =>
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(this.reminderOptions.InitializationTimeout);

                    try
                    {
                        await this.QueueTask(Start).WaitAsync(cts.Token);
                    }
                    catch (Exception exception)
                    {
                        LogErrorStartingReminderService(exception);
                        throw;
                    }
                },
                ct => Task.CompletedTask);
        }

        /// <summary>
        /// Initializes the reminder table.
        /// </summary>
        /// <returns></returns>
        private async Task Initialize(CancellationToken cancellationToken)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(this.reminderOptions.InitializationTimeout);

            // Confirm that it can access the underlying store, as after this the ReminderService will load in the background, without the opportunity to prevent the Silo from starting
            await reminderTable.StartAsync(cts.Token);
        }

        public async override Task Stop()
        {
            await base.Stop();

            listRefreshTimer.Dispose();
            if (this.runTask is { } task)
            {
                await task;
            }

            var disposeTasks = new List<Task>(localReminders.Count);
            foreach (LocalReminderData r in localReminders.Values)
            {
                disposeTasks.Add(r.StopAsync(ReminderEvents.LocalReminderStopReason.ServiceStopped));
            }

            await Task.WhenAll(disposeTasks);
            await reminderTable.StopAsync();

            // For a graceful shutdown, also handover reminder responsibilities to new owner, and update the ReminderTable
            // currently, this is taken care of by periodically reading the reminder table
        }

        public async Task<IGrainReminder> RegisterOrUpdateReminder(GrainId grainId, string reminderName, TimeSpan dueTime, TimeSpan period)
        {
            var entry = new ReminderEntry
            {
                GrainId = grainId,
                ReminderName = reminderName,
                StartAt = _timeProvider.GetUtcNow().UtcDateTime.Add(dueTime),
                Period = period,
            };

            LogDebugRegisterOrUpdateReminder(entry);
            await DoResponsibilitySanityCheck(grainId, "RegisterReminder");
            string? newEtag = await reminderTable.UpsertRow(entry);

            if (newEtag != null)
            {
                LogDebugRegisterReminder(entry, localTableSequence);
                entry.ETag = newEtag;
                AddOrUpdateLocalReminder(entry);

                if (logger.IsEnabled(LogLevel.Trace)) PrintReminders();
                var reminder = new ReminderData(grainId, reminderName, newEtag);
                ReminderEvents.EmitRegistered(grainId, reminderName, Silo);

                return reminder;
            }

            LogErrorRegisterReminder(entry);
            throw new ReminderException($"Could not register reminder {entry} to reminder table due to a race. Please try again later.");
        }

        /// <summary>
        /// Stop the reminder locally, and remove it from the external storage system
        /// </summary>
        /// <param name="reminder"></param>
        /// <returns></returns>
        public async Task UnregisterReminder(IGrainReminder reminder)
        {
            var remData = (ReminderData)reminder;
            LogDebugUnregisterReminder(reminder, localTableSequence);

            var grainId = remData.GrainId;
            string reminderName = remData.ReminderName;
            string eTag = remData.ETag;

            await DoResponsibilitySanityCheck(grainId, "RemoveReminder");

            // it may happen that we dont have this reminder locally ... even then, we attempt to remove the reminder from the reminder
            // table ... the periodic mechanism will stop this reminder at any silo's LocalReminderService that might have this reminder locally

            // remove from persistent/memory store
            var success = await reminderTable.RemoveRow(grainId, reminderName, eTag);
            if (!success)
            {
                success = await IsReminderAlreadyRemoved(grainId, reminderName, reminder);
            }

            if (success)
            {
                if (localReminders.Remove(new(grainId, reminderName), out var localRem))
                {
                    localTableSequence++;
                    ObserveLocalReminderStop(localRem.StopAsync(ReminderEvents.LocalReminderStopReason.Unregistered), grainId, reminderName);
                    LogStoppedReminder(reminder);
                    if (logger.IsEnabled(LogLevel.Trace)) PrintReminders($"After removing {reminder}.");
                }
                else
                {
                    // no-op
                    LogRemovedReminderFromTable(reminder);
                }
                ReminderEvents.EmitUnregistered(grainId, reminderName, Silo);
            }
            else
            {
                LogErrorUnregisterReminder(reminder);
                throw new ReminderException($"Could not unregister reminder {reminder} from the reminder table, due to tag mismatch. You can retry.");
            }
        }

        private async Task<bool> IsReminderAlreadyRemoved(GrainId grainId, string reminderName, IGrainReminder reminder)
        {
            if (await reminderTable.ReadRow(grainId, reminderName) is not null)
            {
                return false;
            }

            LogDebugReminderAlreadyRemoved(reminder);
            return true;
        }

        private void ObserveLocalReminderStop(Task stopTask, GrainId grainId, string reminderName)
        {
            ArgumentNullException.ThrowIfNull(stopTask);

            stopTask.ContinueWith(
                static (task, state) =>
                {
                    var (service, grainId, reminderName) = ((LocalReminderService Service, GrainId GrainId, string ReminderName))state!;
                    service.LogErrorStoppingLocalReminder(task.Exception!, grainId, reminderName);
                },
                (this, grainId, reminderName),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        public async Task<IGrainReminder?> GetReminder(GrainId grainId, string reminderName)
        {
            LogDebugGetReminder(grainId, reminderName);
            ReminderEntry? entry = await reminderTable.ReadRow(grainId, reminderName);
            return entry?.ToIGrainReminder();
        }

        async Task<IGrainReminder> IReminderService.GetReminder(GrainId grainId, string reminderName)
            => (await GetReminder(grainId, reminderName))!;

        public async Task<List<IGrainReminder>> GetReminders(GrainId grainId)
        {
            LogDebugGetReminders(grainId);
            var tableData = await reminderTable.ReadRows(grainId);
            return tableData.Reminders.Select(entry => entry.ToIGrainReminder()).ToList();
        }

        /// <summary>
        /// Attempt to retrieve reminders from the global reminder table
        /// </summary>
        private Task ReadAndUpdateReminders()
        {
            if (StoppedCancellationTokenSource.IsCancellationRequested) return Task.CompletedTask;

            var tasks = new List<Task>();
            RemoveOutOfRangeReminders(tasks);

            // try to retrieve reminders from all my subranges
            var rangeSerialNumberCopy = RangeSerialNumber;
            LogTraceRingRange(RingRange, RangeSerialNumber, localReminders.Count);
            foreach (var range in RangeFactory.GetSubRanges(RingRange))
            {
                tasks.Add(ReadTableAndStartTimers(range, rangeSerialNumberCopy));
            }
            var task = Task.WhenAll(tasks);
            if (logger.IsEnabled(LogLevel.Trace)) task.ContinueWith(_ => PrintReminders(), TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously);
            return task;
        }

        private void RemoveOutOfRangeReminders(List<Task> removedReminderTasks)
        {
            var remindersOutOfRange = 0;

            foreach (var r in localReminders)
            {
                if (RingRange.InRange(r.Key.GrainId)) continue;
                remindersOutOfRange++;

                LogTraceRemovingReminder(r.Value);

                // remove locally
                removedReminderTasks.Add(r.Value.StopAsync(ReminderEvents.LocalReminderStopReason.RemovedFromRange));
                localReminders.Remove(r.Key);
            }

            if (remindersOutOfRange > 0)
            {
                LogInfoRemovedLocalReminders(remindersOutOfRange);
            }
        }

        public override Task OnRangeChange(IRingRange oldRange, IRingRange newRange, bool increased)
        {
            _ = base.OnRangeChange(oldRange, newRange, increased);
            if (Status == GrainServiceStatus.Started)
                return ReadAndUpdateReminders();
            LogIgnoringRangeChange(Status);
            return Task.CompletedTask;
        }

        private async Task RunAsync()
        {
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.ForceYielding);
            TimeSpan? overrideDelay = RandomTimeSpan.Next(InitialReadRetryPeriod);
            while (await listRefreshTimer.NextTick(overrideDelay))
            {
                try
                {
                    overrideDelay = null;
                    switch (Status)
                    {
                        case GrainServiceStatus.Booting:
                            await DoInitialReadAndUpdateReminders();
                            break;
                        case GrainServiceStatus.Started:
                            await ReadAndUpdateReminders();
                            break;
                        default:
                            listRefreshTimer.Dispose();
                            return;
                    }
                }
                catch (Exception exception)
                {
                    LogWarningReadingReminders(exception);
                    overrideDelay = RandomTimeSpan.Next(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20));
                }
            }
        }

        protected override async Task StartInBackground()
        {
            await DoInitialReadAndUpdateReminders();
            this.runTask = RunAsync();
        }

        private async Task DoInitialReadAndUpdateReminders()
        {
            try
            {
                if (StoppedCancellationTokenSource.IsCancellationRequested) return;

                initialReadCallCount++;
                await this.ReadAndUpdateReminders();
                Status = GrainServiceStatus.Started;
                startedTask.TrySetResult(true);
            }
            catch (Exception ex)
            {
                if (StoppedCancellationTokenSource.IsCancellationRequested) return;

                if (initialReadCallCount <= InitialReadRetryCountBeforeFastFailForUpdates)
                {
                    LogWarningInitialLoadFailing(ex, initialReadCallCount);
                }
                else
                {
                    LogErrorInitialLoadFailed(ex, initialReadCallCount);
                    startedTask.TrySetException(new OrleansException("ReminderService failed initial load of reminders and cannot guarantee that the service will be eventually start without manual intervention or restarting the silo.", ex));
                }
            }
        }

        private async Task ReadTableAndStartTimers(ISingleRange range, int rangeSerialNumberCopy)
        {
            LogDebugReadingRows(range);
            localTableSequence++;
            long cachedSequence = localTableSequence;

            try
            {
                var table = await reminderTable.ReadRows(range.Begin, range.End); // get all reminders, even the ones we already have

                if (rangeSerialNumberCopy < RangeSerialNumber)
                {
                    LogDebugRangeChangedWhileFromTable(RangeSerialNumber, rangeSerialNumberCopy);
                    return;
                }

                if (StoppedCancellationTokenSource.IsCancellationRequested) return;

                // If null is a valid value, it means that there's nothing to do.
                if (table is null) return;

                var remindersNotInTable = new Dictionary<ReminderIdentity, LocalReminderData>(); // shallow copy
                foreach (var r in localReminders)
                    if (range.InRange(r.Key.GrainId))
                        remindersNotInTable.Add(r.Key, r.Value);

                LogDebugReadRemindersFromTable(range, table.Reminders.Count, localTableSequence, cachedSequence);
                var tasks = new List<Task>();
                foreach (var entry in table.Reminders)
                {
                    var key = new ReminderIdentity(entry.GrainId, entry.ReminderName);
                    if (localReminders.TryGetValue(key, out var localRem))
                    {
                        if (cachedSequence > localRem.LocalSequenceNumber) // info read from table is same or newer than local info
                        {
                            if (localRem.IsRunning) // if ticking
                            {
                                LogTraceInTableInLocalOldTicking(localRem);
                                // it might happen that our local reminder is different than the one in the table, i.e., eTag is different
                                // if so, update the local timer in place with the new info
                                if (!StringComparer.Ordinal.Equals(localRem.Entry.ETag, entry.ETag))
                                {
                                    LogTraceLocalReminderNeedsUpdate(localRem);
                                    AddOrUpdateLocalReminder(entry);
                                }
                            }
                            else // if not ticking
                            {
                                // no-op
                                LogTraceInTableInLocalOldNotTicking(localRem);
                            }
                        }
                        else // cachedSequence < localRem.LocalSequenceNumber ... // info read from table is older than local info
                        {
                            if (localRem.IsRunning) // if ticking
                            {
                                // no-op
                                LogTraceInTableInLocalNewerTicking(localRem);
                            }
                            else // if not ticking
                            {
                                // no-op
                                LogTraceInTableInLocalNewerNotTicking(localRem);
                            }
                        }
                    }
                    else // exists in table, but not locally
                    {
                        LogTraceInTableNotInLocal(entry);
                        // create and start the reminder
                        AddOrUpdateLocalReminder(entry);
                    }

                    // keep a track of extra reminders ... this 'reminder' is useful, so remove it from extra list
                    remindersNotInTable.Remove(key);
                } // foreach reminder read from table

                int remindersCountBeforeRemove = localReminders.Count;

                // foreach reminder that is not in global table, but exists locally
                foreach (var kv in remindersNotInTable)
                {
                    var reminder = kv.Value;
                    if (cachedSequence < reminder.LocalSequenceNumber)
                    {
                        // no-op
                        LogTraceNotInTableInLocalNewer(reminder);
                    }
                    else // cachedSequence > reminder.LocalSequenceNumber
                    {
                        LogTraceNotInTableInLocalOld(reminder);

                        // remove locally
                        var reminderEntry = reminder.Entry;
                        tasks.Add(reminder.StopAsync(ReminderEvents.LocalReminderStopReason.RemovedFromTable));
                        localReminders.Remove(new(reminderEntry.GrainId, reminderEntry.ReminderName));
                    }
                }

                LogDebugRemovedRemindersFromLocalTable(remindersCountBeforeRemove - localReminders.Count);
                await Task.WhenAll(tasks);
            }
            catch (Exception exc)
            {
                LogErrorFailedToReadTableAndStartTimer(exc);
                throw;
            }
        }

        private void AddOrUpdateLocalReminder(ReminderEntry entry)
        {
            localTableSequence++;
            var key = new ReminderIdentity(entry.GrainId, entry.ReminderName);
            ref var reminderData = ref CollectionsMarshal.GetValueRefOrAddDefault(localReminders, key, out var exists);
            if (exists && reminderData is { } existingReminder)
            {
                existingReminder.LocalSequenceNumber = localTableSequence;
                existingReminder.Update(entry);
                LogDebugUpdatedReminder(entry);
                return;
            }

            var newReminder = new LocalReminderData(entry, this)
            {
                LocalSequenceNumber = localTableSequence,
            };
            newReminder.Start();
            reminderData = newReminder;
            LogDebugStartedReminder(entry);
        }

        private Task DoResponsibilitySanityCheck(GrainId grainId, string debugInfo)
        {
            switch (Status)
            {
                case GrainServiceStatus.Booting:
                    // if service didn't finish the initial load, it could still be loading normally or it might have already
                    // failed a few attempts and callers should not be hold waiting for it to complete
                    var task = this.startedTask.Task;
                    if (task.IsCompleted)
                    {
                        // task at this point is already Faulted
                        task.GetAwaiter().GetResult();
                    }
                    else
                    {
                        return WaitForInitCompletion();
                        async Task WaitForInitCompletion()
                        {
                            try
                            {
                                // wait for the initial load task to complete (with a timeout)
                                await task.WaitAsync(InitialReadMaxWaitTimeForUpdates);
                            }
                            catch (TimeoutException ex)
                            {
                                throw new OrleansException("Reminder Service is still initializing and it is taking a long time. Please retry again later.", ex);
                            }
                            CheckRange();
                        }
                    }
                    break;
                case GrainServiceStatus.Started:
                    break;
                case GrainServiceStatus.Stopped:
                    throw new OperationCanceledException("ReminderService has been stopped.");
                default:
                    throw new InvalidOperationException("status");
            }
            CheckRange();
            return Task.CompletedTask;

            void CheckRange()
            {
                if (!RingRange.InRange(grainId))
                {
                    LogWarningNotResponsible(debugInfo, grainId, RingRange);
                    // For now, we still let the caller proceed without throwing an exception... the periodical mechanism will take care of reminders being registered at the wrong silo
                    // otherwise, we can either reject the request, or re-route the request
                }
            }
        }

        // Note: The list of reminders can be huge in production!
        private void PrintReminders(string? msg = null)
        {
            if (!logger.IsEnabled(LogLevel.Trace)) return;

            var str = $"{(msg ?? "Current list of reminders:")}{Environment.NewLine}{Utils.EnumerableToString(localReminders, null, Environment.NewLine)}";
            LogTraceReminders(str);
        }

        private IRemindable GetGrain(GrainId grainId) => (IRemindable)_referenceActivator.CreateReference(grainId, _grainInterfaceType);

        internal static TimeSpan CalculateInitialDueTime(ReminderEntry entry, DateTime now)
        {
            ArgumentNullException.ThrowIfNull(entry);
            if (entry.Period <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(entry), entry.Period, "Reminder period must be greater than zero.");
            }

            TimeSpan dueTimeSpan;
            if (now < entry.StartAt) // if the time for first tick hasn't passed yet
            {
                dueTimeSpan = entry.StartAt.Subtract(now); // then duetime is duration between now and the first tick time
            }
            else // the first tick happened in the past ... compute duetime based on the first tick time, and period
            {
                // formula used:
                // due = period - 'time passed since last tick (==sinceLast)'
                // due = period - ((Now - FirstTickTime) % period)
                // explanation of formula:
                // (Now - FirstTickTime) => gives amount of time since first tick happened
                // (Now - FirstTickTime) % period => gives amount of time passed since the last tick should have triggered
                var sinceFirstTick = now.Subtract(entry.StartAt);
                var sinceLastTick = TimeSpan.FromTicks(sinceFirstTick.Ticks % entry.Period.Ticks);
                dueTimeSpan = entry.Period.Subtract(sinceLastTick);

                // in corner cases, dueTime can be equal to period ... so, take another mod
                dueTimeSpan = TimeSpan.FromTicks(dueTimeSpan.Ticks % entry.Period.Ticks);
            }

            // PeriodicTimer requires a positive period, so clamp immediate ticks to a small positive delay.
            if (dueTimeSpan < MinimumReminderDueTime)
            {
                dueTimeSpan = MinimumReminderDueTime;
            }

            return dueTimeSpan;
        }

        private sealed class LocalReminderData
        {
            private readonly LocalReminderService _shared;
            private PeriodicTimer? _timer;
            private readonly CancellationTokenSource _stopCancellation = new();
#if NET10_0_OR_GREATER
            private readonly System.Threading.Lock _lock = new();
#else
            private readonly object _lock = new();
#endif
            private ReminderEntry _entry;
            private CancellationTokenSource _scheduleChangedCancellation = new();
            private bool _isFirstTickPending;

            private int _stopReason;
            private long _localSequenceNumber;
            private Task? _runTask;

            internal LocalReminderData(ReminderEntry entry, LocalReminderService reminderService)
            {
                _shared = reminderService;
                _entry = entry;
                _localSequenceNumber = -1;
                _isFirstTickPending = true;
            }

            public ReminderEntry Entry
            {
                get
                {
                    lock (_lock)
                    {
                        return _entry;
                    }
                }
            }

            /// <summary>
            /// Locally, we use this for resolving races between the periodic table reader, and any concurrent local register/unregister requests
            /// </summary>
            public long LocalSequenceNumber
            {
                get
                {
                    lock (_lock)
                    {
                        return _localSequenceNumber;
                    }
                }
                set
                {
                    lock (_lock)
                    {
                        _localSequenceNumber = value;
                    }
                }
            }

            /// <summary>
            /// Gets a value indicating whether this instance is running.
            /// </summary>
            public bool IsRunning
            {
                get
                {
                    lock (_lock)
                    {
                        return _runTask is Task task && !task.IsCompleted;
                    }
                }
            }

            public void Start()
            {
                GrainId grainId;
                string reminderName;
                lock (_lock)
                {
                    if (_runTask is not null)
                    {
                        throw new InvalidOperationException($"{nameof(Start)} may only be called once per instance and has already been called on this instance.");
                    }

                    grainId = _entry.GrainId;
                    reminderName = _entry.ReminderName;
                    using var suppressExecutionContext = new ExecutionContextSuppressor();
                    _runTask = RunAsync(grainId, reminderName);
                }

                ReminderEvents.EmitLocalReminderStarted(grainId, reminderName, this, _shared.Silo);
            }

            public void Update(ReminderEntry entry)
            {
                ArgumentNullException.ThrowIfNull(entry);

                CancellationTokenSource scheduleChangedCancellation;
                PeriodicTimer? timerToDispose;
                lock (_lock)
                {
                    if (_entry.GrainId != entry.GrainId || !StringComparer.Ordinal.Equals(_entry.ReminderName, entry.ReminderName))
                    {
                        throw new InvalidOperationException($"Cannot update reminder {new ReminderIdentity(_entry.GrainId, _entry.ReminderName)} with {entry} because the reminder identity changed.");
                    }

                    _entry = entry;
                    _isFirstTickPending = true;
                    timerToDispose = _timer;
                    _timer = null;
                    scheduleChangedCancellation = _scheduleChangedCancellation;
                    _scheduleChangedCancellation = new();
                }

                scheduleChangedCancellation.Cancel();
                timerToDispose?.Dispose();
            }

            public Task StopAsync(ReminderEvents.LocalReminderStopReason reason)
            {
                ReminderEntry entry;
                PeriodicTimer? timerToDispose;
                CancellationTokenSource scheduleChangedCancellation;
                Task? runTask;
                lock (_lock)
                {
                    entry = _entry;
                    if (_stopReason == (int)ReminderEvents.LocalReminderStopReason.Unknown)
                    {
                        _stopReason = (int)reason;
                    }

                    timerToDispose = _timer;
                    scheduleChangedCancellation = _scheduleChangedCancellation;
                    runTask = _runTask;
                }

                _shared.LogDebugStoppingReminder(entry, reason);
                _stopCancellation.Cancel();
                scheduleChangedCancellation.Cancel();
                timerToDispose?.Dispose();
                return runTask ?? Task.CompletedTask;
            }

            private async Task RunAsync(GrainId grainId, string reminderName)
            {
                await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.ForceYielding);

                try
                {
                    while (await WaitForNextTick())
                    {
                        var entry = PrepareTick();
                        if (entry is null)
                        {
                            continue;
                        }

                        try
                        {
                            var before = _shared._timeProvider.GetUtcNow().UtcDateTime;
                            var status = new TickStatus(entry.StartAt, entry.Period, before);

                            LogTraceTriggeringTick(_shared.logger, this, status, before);
                            ReminderEvents.EmitTickFiring(entry.GrainId, entry.ReminderName, status, _shared.Silo);
                            if (ReminderInstruments.TardinessSeconds.Enabled)
                            {
                                var tardiness = CalculateTardiness(status);
                                ReminderInstruments.TardinessSeconds.Record(tardiness.TotalSeconds);
                            }

                            try
                            {
                                var grainRef = _shared.GetGrain(entry.GrainId);
                                await grainRef.ReceiveReminder(entry.ReminderName, status);

                                if (_shared.logger.IsEnabled(LogLevel.Trace))
                                {
                                    var after = _shared._timeProvider.GetUtcNow().UtcDateTime;
                                    var elapsed = after - before;
                                    LogTraceTickTriggered(_shared.logger, this, elapsed.TotalSeconds, after + entry.Period);
                                }

                                ReminderEvents.EmitTickCompleted(entry.GrainId, entry.ReminderName, status, _shared.Silo);
                                ReminderInstruments.TicksDelivered.Add(1);
                            }
                            catch (Exception exc)
                            {
                                var after = _shared._timeProvider.GetUtcNow().UtcDateTime;
                                LogErrorDeliveringReminderTick(_shared.logger, this, after + entry.Period, exc);
                                ReminderEvents.EmitTickFailed(entry.GrainId, entry.ReminderName, status, exc, _shared.Silo);

                                // What to do with repeated failures to deliver a reminder's ticks?
                            }
                        }
                        catch (Exception exception)
                        {
                            LogWarningFiringReminder(_shared.logger, entry.ReminderName, entry.GrainId, exception);
                        }
                    }
                }
                finally
                {
                    ReminderEvents.EmitLocalReminderStopped(
                        grainId,
                        reminderName,
                        this,
                        (ReminderEvents.LocalReminderStopReason)_stopReason,
                        _shared.Silo);
                }
            }

            private async Task<bool> WaitForNextTick()
            {
                while (true)
                {
                    TimeSpan? initialDueTime;
                    PeriodicTimer? periodicTimer;
                    CancellationToken scheduleChangedToken;
                    lock (_lock)
                    {
                        if (_stopReason != (int)ReminderEvents.LocalReminderStopReason.Unknown)
                        {
                            return false;
                        }

                        if (_isFirstTickPending)
                        {
                            _isFirstTickPending = false;
                            initialDueTime = GetInitialDueTime(_entry);
                        }
                        else
                        {
                            initialDueTime = null;
                            _timer ??= new(_entry.Period, _shared._timeProvider);
                        }

                        periodicTimer = _timer;
                        scheduleChangedToken = _scheduleChangedCancellation.Token;
                    }

                    using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(_stopCancellation.Token, scheduleChangedToken);
                    try
                    {
                        if (initialDueTime is { } delay)
                        {
                            await Task.Delay(delay, _shared._timeProvider, waitCancellation.Token);
                            if (!TryStartPeriodicTimer(scheduleChangedToken))
                            {
                                if (_stopCancellation.IsCancellationRequested)
                                {
                                    return false;
                                }

                                continue;
                            }

                            return true;
                        }

                        var result = await periodicTimer!.WaitForNextTickAsync(waitCancellation.Token);
                        if (!result && scheduleChangedToken.IsCancellationRequested)
                        {
                            continue;
                        }

                        return result;
                    }
                    catch (OperationCanceledException) when (_stopCancellation.IsCancellationRequested)
                    {
                        return false;
                    }
                    catch (OperationCanceledException) when (scheduleChangedToken.IsCancellationRequested)
                    {
                        continue;
                    }
                }
            }

            private bool TryStartPeriodicTimer(CancellationToken scheduleChangedToken)
            {
                lock (_lock)
                {
                    if (_stopReason != (int)ReminderEvents.LocalReminderStopReason.Unknown || scheduleChangedToken.IsCancellationRequested || _isFirstTickPending)
                    {
                        return false;
                    }

                    _timer ??= new(_entry.Period, _shared._timeProvider);
                    return true;
                }
            }

            private ReminderEntry? PrepareTick()
            {
                lock (_lock)
                {
                    if (_stopReason != (int)ReminderEvents.LocalReminderStopReason.Unknown)
                    {
                        return null;
                    }

                    if (_isFirstTickPending)
                    {
                        return null;
                    }

                    return _entry;
                }
            }

            private TimeSpan GetInitialDueTime(ReminderEntry entry)
            {
                return CalculateInitialDueTime(entry, _shared._timeProvider.GetUtcNow().UtcDateTime);
            }

            private static TimeSpan CalculateTardiness(TickStatus status)
            {
                if (status.Period <= TimeSpan.Zero || status.CurrentTickTime <= status.FirstTickTime)
                {
                    return TimeSpan.Zero;
                }

                var sinceFirstTick = status.CurrentTickTime - status.FirstTickTime;
                return TimeSpan.FromTicks(sinceFirstTick.Ticks % status.Period.Ticks);
            }

            public override string ToString()
            {
                lock (_lock)
                {
                    var isRunning = _runTask is Task task && !task.IsCompleted;
                    return $"[{_entry.ReminderName}, {_entry.GrainId}, {_entry.Period}, {LogFormatter.PrintDate(_entry.StartAt)}, {_entry.ETag}, {_localSequenceNumber}, {(isRunning ? "Ticking" : "Stopped")}]";
                }
            }
        }

        private readonly struct ReminderIdentity(GrainId grainId, string reminderName) : IEquatable<ReminderIdentity>
        {
            public readonly GrainId GrainId = grainId;
            public readonly string ReminderName = reminderName;

            public readonly bool Equals(ReminderIdentity other) => GrainId.Equals(other.GrainId) && ReminderName.Equals(other.ReminderName);

            public override readonly bool Equals(object? other) => other is ReminderIdentity id && Equals(id);

            public override readonly int GetHashCode() => HashCode.Combine(GrainId, ReminderName);
        }

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Error activating reminder service."
        )]
        private partial void LogErrorActivatingReminderService(Exception exception);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Error stopping reminder service."
        )]
        private partial void LogErrorStoppingReminderService(Exception exception);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Error starting reminder service."
        )]
        private partial void LogErrorStartingReminderService(Exception exception);

        [LoggerMessage(
            Level = LogLevel.Debug,
            EventId = (int)ErrorCode.RS_RegisterOrUpdate,
            Message = "RegisterOrUpdateReminder: {Entry}"
        )]
        private partial void LogDebugRegisterOrUpdateReminder(ReminderEntry entry);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Registered reminder {Entry} in table, assigned localSequence {LocalSequence}"
        )]
        private partial void LogDebugRegisterReminder(ReminderEntry entry, long localSequence);

        [LoggerMessage(
            Level = LogLevel.Error,
            EventId = (int)ErrorCode.RS_Register_TableError,
            Message = "Could not register reminder {Entry} to reminder table due to a race. Please try again later."
        )]
        private partial void LogErrorRegisterReminder(ReminderEntry entry);

        [LoggerMessage(
            Level = LogLevel.Debug,
            EventId = (int)ErrorCode.RS_Unregister,
            Message = "UnregisterReminder: {Entry}, LocalTableSequence: {LocalTableSequence}"
        )]
        private partial void LogDebugUnregisterReminder(IGrainReminder entry, long localTableSequence);

        [LoggerMessage(
            Level = LogLevel.Debug,
            EventId = (int)ErrorCode.RS_Stop,
            Message = "Requested stop for reminder {Entry}"
        )]
        private partial void LogStoppedReminder(IGrainReminder entry);

        [LoggerMessage(
            Level = LogLevel.Debug,
            EventId = (int)ErrorCode.RS_RemoveFromTable,
            Message = "Removed reminder from table which I didn't have locally: {Entry}."
        )]
        private partial void LogRemovedReminderFromTable(IGrainReminder entry);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Reminder was already absent from the reminder table during unregister: {Entry}."
        )]
        private partial void LogDebugReminderAlreadyRemoved(IGrainReminder entry);

        [LoggerMessage(
            Level = LogLevel.Error,
            EventId = (int)ErrorCode.RS_Unregister_TableError,
            Message = "Could not unregister reminder {Reminder} from the reminder table, due to tag mismatch. You can retry."
        )]
        private partial void LogErrorUnregisterReminder(IGrainReminder reminder);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Local reminder stop failed for GrainId={GrainId}, ReminderName={ReminderName}"
        )]
        private partial void LogErrorStoppingLocalReminder(Exception exception, GrainId grainId, string reminderName);

        [LoggerMessage(
            Level = LogLevel.Debug,
            EventId = (int)ErrorCode.RS_GetReminder,
            Message = "GetReminder: GrainId={GrainId} ReminderName={ReminderName}"
        )]
        private partial void LogDebugGetReminder(GrainId grainId, string reminderName);

        [LoggerMessage(
            Level = LogLevel.Debug,
            EventId = (int)ErrorCode.RS_GetReminders,
            Message = "GetReminders: GrainId={GrainId}"
        )]
        private partial void LogDebugGetReminders(GrainId grainId);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "My range {RingRange}, RangeSerialNumber {RangeSerialNumber}. Local reminders count {LocalRemindersCount}"
        )]
        private partial void LogTraceRingRange(IRingRange ringRange, int rangeSerialNumber, int localRemindersCount);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Not in my range anymore, so removing. {Reminder}"
        )]
        private partial void LogTraceRemovingReminder(LocalReminderData reminder);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Removed {RemovedCount} local reminders that are now out of my range."
        )]
        private partial void LogInfoRemovedLocalReminders(int removedCount);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Ignoring range change until ReminderService is Started -- Current status = {Status}"
        )]
        private partial void LogIgnoringRangeChange(GrainServiceStatus status);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Exception while reading reminders"
        )]
        private partial void LogWarningReadingReminders(Exception exception);

        [LoggerMessage(
            Level = LogLevel.Warning,
            EventId = (int)ErrorCode.RS_ServiceInitialLoadFailing,
            Message = "ReminderService failed initial load of reminders and will retry. Attempt #{AttemptNumber}"
        )]
        private partial void LogWarningInitialLoadFailing(Exception exception, uint attemptNumber);

        [LoggerMessage(
            Level = LogLevel.Error,
            EventId = (int)ErrorCode.RS_ServiceInitialLoadFailed,
            Message = "ReminderService failed initial load of reminders and cannot guarantee that the service will be eventually start without manual intervention or restarting the silo. Attempt #{AttemptNumber}"
        )]
        private partial void LogErrorInitialLoadFailed(Exception exception, uint attemptNumber);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Reading rows from {Range}"
        )]
        private partial void LogDebugReadingRows(IRingRange range);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "My range changed while reading from the table, ignoring the results. Another read has been started. RangeSerialNumber {RangeSerialNumber}, RangeSerialNumberCopy {RangeSerialNumberCopy}."
        )]
        private partial void LogDebugRangeChangedWhileFromTable(int rangeSerialNumber, int rangeSerialNumberCopy);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "For range {Range}, I read in {ReminderCount} reminders from table. LocalTableSequence {LocalTableSequence}, CachedSequence {CachedSequence}"
        )]
        private partial void LogDebugReadRemindersFromTable(IRingRange range, int reminderCount, long localTableSequence, long cachedSequence);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "In table, In local, Old, & Ticking {LocalReminder}"
        )]
        private partial void LogTraceInTableInLocalOldTicking(LocalReminderData localReminder);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "{LocalReminder} needs an in-place update"
        )]
        private partial void LogTraceLocalReminderNeedsUpdate(LocalReminderData localReminder);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "In table, In local, Old, & Not Ticking {LocalReminder}"
        )]
        private partial void LogTraceInTableInLocalOldNotTicking(LocalReminderData localReminder);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "In table, In local, Newer, & Ticking {LocalReminder}"
        )]
        private partial void LogTraceInTableInLocalNewerTicking(LocalReminderData localReminder);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "In table, In local, Newer, & Not Ticking {LocalReminder}"
        )]
        private partial void LogTraceInTableInLocalNewerNotTicking(LocalReminderData localReminder);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "In table, Not in local, {Reminder}"
        )]
        private partial void LogTraceInTableNotInLocal(ReminderEntry reminder);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Not in table, In local, Newer, {Reminder}"
        )]
        private partial void LogTraceNotInTableInLocalNewer(LocalReminderData reminder);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Not in table, In local, Old, so removing. {Reminder}"
        )]
        private partial void LogTraceNotInTableInLocalOld(LocalReminderData reminder);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "{Message}"
        )]
        private partial void LogTraceReminders(string message);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Removed {RemovedCount} reminders from local table"
        )]
        private partial void LogDebugRemovedRemindersFromLocalTable(int removedCount);

        [LoggerMessage(
            Level = LogLevel.Error,
            EventId = (int)ErrorCode.RS_FailedToReadTableAndStartTimer,
            Message = "Failed to read rows from table."
        )]
        private partial void LogErrorFailedToReadTableAndStartTimer(Exception exception);

        [LoggerMessage(
            Level = LogLevel.Debug,
            EventId = (int)ErrorCode.RS_LocalStop,
            Message = "Locally stopping reminder {Reminder} with reason {Reason}"
        )]
        private partial void LogDebugStoppingReminder(ReminderEntry reminder, ReminderEvents.LocalReminderStopReason reason);

        [LoggerMessage(
            Level = LogLevel.Debug,
            EventId = (int)ErrorCode.RS_Started,
            Message = "Started reminder {Reminder}."
        )]
        private partial void LogDebugStartedReminder(ReminderEntry reminder);

        [LoggerMessage(
            Level = LogLevel.Debug,
            EventId = (int)ErrorCode.RS_Started,
            Message = "Updated reminder {Reminder} in place."
        )]
        private partial void LogDebugUpdatedReminder(ReminderEntry reminder);

        [LoggerMessage(
            Level = LogLevel.Warning,
            EventId = (int)ErrorCode.RS_NotResponsible,
            Message = "I shouldn't have received request '{Request}' for {GrainId}. It is not in my responsibility range: {Range}"
        )]
        private partial void LogWarningNotResponsible(string request, GrainId grainId, IRingRange range);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Exception firing reminder \"{ReminderName}\" for grain {GrainId}"
        )]
        private static partial void LogWarningFiringReminder(ILogger logger, string reminderName, GrainId grainId, Exception exception);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Triggering tick for {Instance}, status {Status}, now {CurrentTime}"
        )]
        private static partial void LogTraceTriggeringTick(ILogger logger, LocalReminderData instance, TickStatus status, DateTime currentTime);

        private void LogTraceTickTriggeredHelper(LocalReminderData instance, double dueTime, DateTime nextDueTime)
        {
            if (logger.IsEnabled(LogLevel.Trace))
            {

            }
        }

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Tick triggered for {Instance}, dt {DueTime} sec, next@~ {NextDueTime}"
        )]
        private static partial void LogTraceTickTriggered(ILogger logger, LocalReminderData instance, double dueTime, DateTime nextDueTime);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Could not deliver reminder tick for {Instance}, next {NextDueTime}."
        )]
        private static partial void LogErrorDeliveringReminderTick(ILogger logger, LocalReminderData instance, DateTime nextDueTime, Exception exception);
    }
}
