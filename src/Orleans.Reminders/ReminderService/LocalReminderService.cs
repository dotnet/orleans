using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.CodeGeneration;
using Orleans.GrainReferences;
using Orleans.Hosting;
using Orleans.Internal;
using Orleans.Metadata;
using Orleans.Runtime.ConsistentRing;
using Orleans.Runtime.Internal;
using Orleans.Runtime.Scheduler;
using static Orleans.InterfaceToImplementationMappingCache;

namespace Orleans.Runtime.ReminderService
{
    internal sealed class LocalReminderService : GrainService, IReminderService, ILifecycleParticipant<ISiloLifecycle>
    {
        private const int InitialReadRetryCountBeforeFastFailForUpdates = 2;
        private static readonly TimeSpan InitialReadMaxWaitTimeForUpdates = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan InitialReadRetryPeriod = TimeSpan.FromSeconds(30);
        private readonly ILogger logger;
        private readonly ReminderOptions reminderOptions;
        private readonly Dictionary<ReminderIdentity, LocalReminderData> localReminders = new();
        private readonly IReminderTable reminderTable;
        private readonly TaskCompletionSource<bool> startedTask;
        private readonly TimeSpan initTimeout;
        private readonly IAsyncTimerFactory asyncTimerFactory;
        private readonly IAsyncTimer listRefreshTimer; // timer that refreshes our list of reminders to reflect global reminder table
        private readonly GrainReferenceActivator _referenceActivator;
        private readonly GrainInterfaceType _grainInterfaceType;
        private long localTableSequence;
        private uint initialReadCallCount = 0;
        private Task runTask;

        public LocalReminderService(
            GrainReferenceActivator referenceActivator,
            GrainInterfaceTypeResolver interfaceTypeResolver,
            ILocalSiloDetails localSiloDetails,
            IReminderTable reminderTable,
            ILoggerFactory loggerFactory,
            IAsyncTimerFactory asyncTimerFactory,
            IOptions<ReminderOptions> reminderOptions,
            IConsistentRingProvider ringProvider,
            Catalog catalog)
            : base(
                  SystemTargetGrainId.CreateGrainServiceGrainId(GrainInterfaceUtils.GetGrainClassTypeCode(typeof(IReminderService)), null, localSiloDetails.SiloAddress),
                  localSiloDetails.SiloAddress,
                  loggerFactory,
                  ringProvider)
        {
            _referenceActivator = referenceActivator;
            _grainInterfaceType = interfaceTypeResolver.GetGrainInterfaceType(typeof(IRemindable));
            this.reminderOptions = reminderOptions.Value;
            this.initTimeout = this.reminderOptions.InitializationTimeout;
            this.reminderTable = reminderTable;
            this.asyncTimerFactory = asyncTimerFactory;
            ReminderInstruments.RegisterActiveRemindersObserve(() => localReminders.Count);
            startedTask = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            this.logger = loggerFactory.CreateLogger<LocalReminderService>();
            this.listRefreshTimer = asyncTimerFactory.Create(this.reminderOptions.RefreshReminderListPeriod, "ReminderService.ReminderListRefresher");
            catalog.RegisterSystemTarget(this);
        }

        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle observer)
        {
            observer.Subscribe(
                nameof(LocalReminderService),
                ServiceLifecycleStage.Active,
                async ct =>
                {
                    using var timeoutCancellation = new CancellationTokenSource(initTimeout);
                    var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCancellation.Token);
                    await this.QueueTask(Start)
                        .WithCancellation(ct, $"Starting ReminderService failed due to timeout {initTimeout}");
                },
                ct =>
                {
                    return this.QueueTask(Stop)
                        .WithCancellation(ct, "Stopping ReminderService failed because the task was cancelled");
                });
        }

        /// <summary>
        /// Attempt to retrieve reminders, that are my responsibility, from the global reminder table when starting this silo (reminder service instance)
        /// </summary>
        /// <returns></returns>
        public override async Task Start()
        {
            // confirm that it can access the underlying store, as after this the ReminderService will load in the background, without the opportunity to prevent the Silo from starting
            await reminderTable.Init().WithTimeout(initTimeout, $"ReminderTable Initialization failed due to timeout {initTimeout}");

            _ = base.Start();
        }

        public async override Task Stop()
        {
            _ = base.Stop();

            if (listRefreshTimer != null)
            {
                listRefreshTimer.Dispose();
                if (this.runTask is Task task)
                {
                    await task;
                }
            }

            foreach (LocalReminderData r in localReminders.Values)
            {
                r.StopReminder();
            }

            // for a graceful shutdown, also handover reminder responsibilities to new owner, and update the ReminderTable
            // currently, this is taken care of by periodically reading the reminder table
        }

        public async Task<IGrainReminder> RegisterOrUpdateReminder(GrainId grainId, string reminderName, TimeSpan dueTime, TimeSpan period)
        {
            var entry = new ReminderEntry
            {
                GrainId = grainId,
                ReminderName = reminderName,
                StartAt = DateTime.UtcNow.Add(dueTime),
                Period = period,
            };

            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug((int)ErrorCode.RS_RegisterOrUpdate, "RegisterOrUpdateReminder: {Entry}", entry.ToString());
            await DoResponsibilitySanityCheck(grainId, "RegisterReminder");
            var newEtag = await reminderTable.UpsertRow(entry);

            if (newEtag != null)
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("Registered reminder {Entry} in table, assigned localSequence {LocalSequence}", entry, localTableSequence);
                entry.ETag = newEtag;
                StartAndAddTimer(entry);
                if (logger.IsEnabled(LogLevel.Trace)) PrintReminders();
                return new ReminderData(grainId, reminderName, newEtag);
            }

            logger.LogError((int)ErrorCode.RS_Register_TableError, "Could not register reminder {Entry} to reminder table due to a race. Please try again later.", entry);
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
            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug((int)ErrorCode.RS_Unregister, "UnregisterReminder: {Entry}, LocalTableSequence: {LocalTableSequence}", remData, localTableSequence);

            var grainId = remData.GrainId;
            string reminderName = remData.ReminderName;
            string eTag = remData.ETag;

            await DoResponsibilitySanityCheck(grainId, "RemoveReminder");

            // it may happen that we dont have this reminder locally ... even then, we attempt to remove the reminder from the reminder
            // table ... the periodic mechanism will stop this reminder at any silo's LocalReminderService that might have this reminder locally

            // remove from persistent/memory store
            var success = await reminderTable.RemoveRow(grainId, reminderName, eTag);
            if (success)
            {
                bool removed = TryStopPreviousTimer(grainId, reminderName);
                if (removed)
                {
                    if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug((int)ErrorCode.RS_Stop, "Stopped reminder {Entry}", reminder);
                    if (logger.IsEnabled(LogLevel.Trace)) PrintReminders($"After removing {reminder}.");
                }
                else
                {
                    // no-op
                    if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug((int)ErrorCode.RS_RemoveFromTable, "Removed reminder from table which I didn't have locally: {Entry}.", reminder);
                }
            }
            else
            {
                logger.LogError((int)ErrorCode.RS_Unregister_TableError, "Could not unregister reminder {Reminder} from the reminder table, due to tag mismatch. You can retry.", reminder);
                throw new ReminderException($"Could not unregister reminder {reminder} from the reminder table, due to tag mismatch. You can retry.");
            }
        }

        public async Task<IGrainReminder> GetReminder(GrainId grainId, string reminderName)
        {
            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug((int)ErrorCode.RS_GetReminder, "GetReminder: GrainId={GrainId} ReminderName={ReminderName}", grainId.ToString(), reminderName);
            var entry = await reminderTable.ReadRow(grainId, reminderName);
            return entry == null ? null : entry.ToIGrainReminder();
        }

        public async Task<List<IGrainReminder>> GetReminders(GrainId grainId)
        {
            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug((int)ErrorCode.RS_GetReminders, "GetReminders: GrainId={GrainId}", grainId.ToString());
            var tableData = await reminderTable.ReadRows(grainId);
            return tableData.Reminders.Select(entry => entry.ToIGrainReminder()).ToList();
        }

        /// <summary>
        /// Attempt to retrieve reminders from the global reminder table
        /// </summary>
        private Task ReadAndUpdateReminders()
        {
            if (StoppedCancellationTokenSource.IsCancellationRequested) return Task.CompletedTask;

            RemoveOutOfRangeReminders();

            // try to retrieve reminders from all my subranges
            var rangeSerialNumberCopy = RangeSerialNumber;
            if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace("My range {RingRange}, RangeSerialNumber {RangeSerialNumber}. Local reminders count {LocalRemindersCount}", RingRange, RangeSerialNumber, localReminders.Count);
            var acks = new List<Task>();
            foreach (var range in RangeFactory.GetSubRanges(RingRange))
            {
                acks.Add(ReadTableAndStartTimers(range, rangeSerialNumberCopy));
            }
            var task = Task.WhenAll(acks);
            if (logger.IsEnabled(LogLevel.Trace)) task.ContinueWith(_ => PrintReminders(), TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously);
            return task;
        }

        private void RemoveOutOfRangeReminders()
        {
            var remindersOutOfRange = 0;

            foreach (var r in localReminders)
            {
                if (RingRange.InRange(r.Key.GrainId)) continue;
                remindersOutOfRange++;

                if (logger.IsEnabled(LogLevel.Trace))
                    logger.LogTrace("Not in my range anymore, so removing. {Reminder}", r.Value);
                // remove locally
                r.Value.StopReminder();
                localReminders.Remove(r.Key);
            }

            if (remindersOutOfRange > 0 && logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Removed {RemovedCount} local reminders that are now out of my range.", remindersOutOfRange);
            }
        }

        public override Task OnRangeChange(IRingRange oldRange, IRingRange newRange, bool increased)
        {
            _ = base.OnRangeChange(oldRange, newRange, increased);
            if (Status == GrainServiceStatus.Started)
                return ReadAndUpdateReminders();
            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("Ignoring range change until ReminderService is Started -- Current status = {Status}", Status);
            return Task.CompletedTask;
        }

        private async Task RunAsync()
        {
            await Task.Yield();
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
                    this.logger.LogWarning(exception, "Exception while reading reminders");
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
                    logger.LogWarning(
                        (int)ErrorCode.RS_ServiceInitialLoadFailing,
                        ex,
                        "ReminderService failed initial load of reminders and will retry. Attempt #{AttemptNumber}",
                        this.initialReadCallCount);
                }
                else
                {
                    logger.LogError(
                        (int)ErrorCode.RS_ServiceInitialLoadFailed,
                        ex,
                        "ReminderService failed initial load of reminders and cannot guarantee that the service will be eventually start without manual intervention or restarting the silo. Attempt #{AttemptNumber}", this.initialReadCallCount);
                    startedTask.TrySetException(new OrleansException("ReminderService failed initial load of reminders and cannot guarantee that the service will be eventually start without manual intervention or restarting the silo.", ex));
                }
            }
        }

        private async Task ReadTableAndStartTimers(ISingleRange range, int rangeSerialNumberCopy)
        {
            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("Reading rows from {Range}", range.ToString());
            localTableSequence++;
            long cachedSequence = localTableSequence;

            try
            {
                var table = await reminderTable.ReadRows(range.Begin, range.End); // get all reminders, even the ones we already have

                if (rangeSerialNumberCopy < RangeSerialNumber)
                {
                    if (logger.IsEnabled(LogLevel.Debug))
                    {
                        logger.LogDebug(
                        "My range changed while reading from the table, ignoring the results. Another read has been started. RangeSerialNumber {RangeSerialNumber}, RangeSerialNumberCopy {RangeSerialNumberCopy}.",
                        RangeSerialNumber,
                        rangeSerialNumberCopy);
                    }

                    return;
                }

                if (StoppedCancellationTokenSource.IsCancellationRequested) return;

                // If null is a valid value, it means that there's nothing to do.
                if (table is null) return;

                var remindersNotInTable = new Dictionary<ReminderIdentity, LocalReminderData>(); // shallow copy
                foreach (var r in localReminders)
                    if (range.InRange(r.Key.GrainId))
                        remindersNotInTable.Add(r.Key, r.Value);

                if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug(
                        "For range {Range}, I read in {ReminderCount} reminders from table. LocalTableSequence {LocalTableSequence}, CachedSequence {CachedSequence}",
                        range.ToString(),
                        table.Reminders.Count,
                        localTableSequence,
                        cachedSequence);
                }

                foreach (var entry in table.Reminders)
                {
                    var key = new ReminderIdentity(entry.GrainId, entry.ReminderName);
                    if (localReminders.TryGetValue(key, out var localRem))
                    {
                        if (cachedSequence > localRem.LocalSequenceNumber) // info read from table is same or newer than local info
                        {
                            if (localRem.IsRunning) // if ticking
                            {
                                if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace("In table, In local, Old, & Ticking {LocalReminder}", localRem);
                                // it might happen that our local reminder is different than the one in the table, i.e., eTag is different
                                // if so, stop the local timer for the old reminder, and start again with new info
                                if (!localRem.ETag.Equals(entry.ETag))
                                // this reminder needs a restart
                                {
                                    if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace("{LocalReminder} Needs a restart", localRem);
                                    localRem.StopReminder();
                                    localReminders.Remove(localRem.Identity);
                                    StartAndAddTimer(entry);
                                }
                            }
                            else // if not ticking
                            {
                                // no-op
                                if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace("In table, In local, Old, & Not Ticking {LocalReminder}", localRem);
                            }
                        }
                        else // cachedSequence < localRem.LocalSequenceNumber ... // info read from table is older than local info
                        {
                            if (localRem.IsRunning) // if ticking
                            {
                                // no-op
                                if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace("In table, In local, Newer, & Ticking {LocalReminder}", localRem);
                            }
                            else // if not ticking
                            {
                                // no-op
                                if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace("In table, In local, Newer, & Not Ticking {LocalReminder}", localRem);
                            }
                        }
                    }
                    else // exists in table, but not locally
                    {
                        if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace("In table, Not in local, {Reminder}", entry);
                        // create and start the reminder
                        StartAndAddTimer(entry);
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
                        if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace("Not in table, In local, Newer, {Reminder}", reminder);
                    }
                    else // cachedSequence > reminder.LocalSequenceNumber
                    {
                        if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace("Not in table, In local, Old, so removing. {Reminder}", reminder);
                        // remove locally
                        reminder.StopReminder();
                        localReminders.Remove(reminder.Identity);
                    }
                }
                if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("Removed {RemovedCount} reminders from local table", localReminders.Count - remindersCountBeforeRemove);
            }
            catch (Exception exc)
            {
                logger.LogError((int)ErrorCode.RS_FailedToReadTableAndStartTimer, exc, "Failed to read rows from table.");
                throw;
            }
        }

        private void StartAndAddTimer(ReminderEntry entry)
        {
            // it might happen that we already have a local reminder with a different eTag
            // if so, stop the local timer for the old reminder, and start again with new info
            // Note: it can happen here that we restart a reminder that has the same eTag as what we just registered ... its a rare case, and restarting it doesn't hurt, so we don't check for it
            if (localReminders.TryGetValue(new(entry.GrainId, entry.ReminderName), out var prevReminder)) // if found locally
            {
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug(
                    (int)ErrorCode.RS_LocalStop,
                    "Locally stopping reminder {PreviousReminder} as it is different than newly registered reminder {Reminder}",
                    prevReminder,
                    entry);
                }

                prevReminder.StopReminder();
                localReminders.Remove(prevReminder.Identity);
            }

            var newReminder = new LocalReminderData(entry, this);
            localTableSequence++;
            newReminder.LocalSequenceNumber = localTableSequence;
            localReminders.Add(newReminder.Identity, newReminder);
            newReminder.StartTimer();
            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug((int)ErrorCode.RS_Started, "Started reminder {Reminder}.", entry.ToString());
        }

        // stop without removing it. will remove later.
        private bool TryStopPreviousTimer(GrainId grainId, string reminderName)
        {
            // we stop the locally running timer for this reminder
            if (!localReminders.TryGetValue(new(grainId, reminderName), out var localRem)) return false;

            // if we have it locally
            localTableSequence++; // move to next sequence
            localRem.LocalSequenceNumber = localTableSequence;
            localRem.StopReminder();
            return true;
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
                                await task.WithTimeout(InitialReadMaxWaitTimeForUpdates);
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
                    logger.LogWarning((int)ErrorCode.RS_NotResponsible, "I shouldn't have received request '{Request}' for {GrainId}. It is not in my responsibility range: {Range}",
                        debugInfo, grainId.ToString(), RingRange);
                    // For now, we still let the caller proceed without throwing an exception... the periodical mechanism will take care of reminders being registered at the wrong silo
                    // otherwise, we can either reject the request, or re-route the request
                }
            }
        }

        // Note: The list of reminders can be huge in production!
        private void PrintReminders(string msg = null)
        {
            if (!logger.IsEnabled(LogLevel.Trace)) return;

            var str = $"{(msg ?? "Current list of reminders:")}{Environment.NewLine}{Utils.EnumerableToString(localReminders, null, Environment.NewLine)}";
            logger.LogTrace("{Message}", str);
        }

        private IRemindable GetGrain(GrainId grainId) => (IRemindable)_referenceActivator.CreateReference(grainId, _grainInterfaceType);

        private sealed class LocalReminderData
        {
            private readonly IRemindable remindable;
            private readonly DateTime firstTickTime; // time for the first tick of this reminder
            private readonly TimeSpan period;
            private readonly ILogger logger;
            private readonly IAsyncTimer timer;

            private ValueStopwatch stopwatch;
            private Task runTask;

            internal LocalReminderData(ReminderEntry entry, LocalReminderService reminderService)
            {
                Identity = new ReminderIdentity(entry.GrainId, entry.ReminderName);
                firstTickTime = entry.StartAt;
                period = entry.Period;
                remindable = reminderService.GetGrain(entry.GrainId);
                ETag = entry.ETag;
                LocalSequenceNumber = -1;
                logger = reminderService.logger;
                this.timer = reminderService.asyncTimerFactory.Create(period, "");
            }

            public ReminderIdentity Identity { get; }

            public string ETag { get; }

            /// <summary>
            /// Locally, we use this for resolving races between the periodic table reader, and any concurrent local register/unregister requests
            /// </summary>
            public long LocalSequenceNumber { get; set; }

            /// <summary>
            /// Gets a value indicating whether this instance is running.
            /// </summary>
            public bool IsRunning => runTask is Task task && !task.IsCompleted;

            public void StartTimer()
            {
                if (runTask is null)
                {
                    using var suppressExecutionContext = new ExecutionContextSuppressor();
                    this.runTask = this.RunAsync();
                }
                else
                {
                    throw new InvalidOperationException($"{nameof(StartTimer)} may only be called once per instance and has already been called on this instance.");
                }
            }

            public void StopReminder()
            {
                timer.Dispose();
            }

            private async Task RunAsync()
            {
                TimeSpan? dueTimeSpan = CalculateDueTime();
                while (await this.timer.NextTick(dueTimeSpan))
                {
                    try
                    {
                        await OnTimerTick();
                        ReminderInstruments.TicksDelivered.Add(1);
                    }
                    catch (Exception exception)
                    {
                        logger.LogWarning(
                            exception,
                            "Exception firing reminder \"{ReminderName}\" for grain {GrainId}",
                            this.Identity.ReminderName,
                            this.Identity.GrainId);
                    }

                    dueTimeSpan = CalculateDueTime();
                }
            }

            private TimeSpan CalculateDueTime()
            {
                TimeSpan dueTimeSpan;
                var now = DateTime.UtcNow;
                if (now < firstTickTime) // if the time for first tick hasn't passed yet
                {
                    dueTimeSpan = firstTickTime.Subtract(now); // then duetime is duration between now and the first tick time
                }
                else // the first tick happened in the past ... compute duetime based on the first tick time, and period
                {
                    // formula used:
                    // due = period - 'time passed since last tick (==sinceLast)'
                    // due = period - ((Now - FirstTickTime) % period)
                    // explanation of formula:
                    // (Now - FirstTickTime) => gives amount of time since first tick happened
                    // (Now - FirstTickTime) % period => gives amount of time passed since the last tick should have triggered
                    var sinceFirstTick = now.Subtract(firstTickTime);
                    var sinceLastTick = TimeSpan.FromTicks(sinceFirstTick.Ticks % period.Ticks);
                    dueTimeSpan = period.Subtract(sinceLastTick);

                    // in corner cases, dueTime can be equal to period ... so, take another mod
                    dueTimeSpan = TimeSpan.FromTicks(dueTimeSpan.Ticks % period.Ticks);
                }

                // If the previous tick took no percievable time, be sure to wait at least one period until the next tick.
                // If the previous tick took one period or greater, then we will skip up to one period.
                // That is preferable over double-firing for fast ticks, which are expected to be more common.
                if (dueTimeSpan <= TimeSpan.FromMilliseconds(30))
                {
                    dueTimeSpan = period;
                }

                return dueTimeSpan;
            }

            public async Task OnTimerTick()
            {
                var before = DateTime.UtcNow;
                var status = TickStatus.Create(firstTickTime, period, before);

                if (logger.IsEnabled(LogLevel.Trace))
                {
                    logger.LogTrace("Triggering tick for {Instance}, status {Status}, now {CurrentTime}", this.ToString(), status, before);
                }

                try
                {
                    if (stopwatch.IsRunning)
                    {
                        stopwatch.Stop();
                        var tardiness = stopwatch.Elapsed - period;
                        ReminderInstruments.TardinessSeconds.Record(Math.Max(0, tardiness.TotalSeconds));
                    }

                    await remindable.ReceiveReminder(Identity.ReminderName, status);

                    stopwatch.Restart();

                    var after = DateTime.UtcNow;
                    if (logger.IsEnabled(LogLevel.Trace))
                    {
                        // the next tick isn't actually scheduled until we return control to
                        // AsyncSafeTimer but we can approximate it by adding the period of the reminder
                        // to the after time.
                        logger.LogTrace(
                            "Tick triggered for {Instance}, dt {DueTime} sec, next@~ {NextDueTime}",
                            this.ToString(),
                            (after - before).TotalSeconds,
                            after + this.period);
                    }
                }
                catch (Exception exc)
                {
                    var after = DateTime.UtcNow;
                    logger.LogError(
                        (int)ErrorCode.RS_Tick_Delivery_Error,
                        exc,
                        "Could not deliver reminder tick for {Instance}, next {NextDueTime}.",
                        this.ToString(),
                        after + this.period);
                    // What to do with repeated failures to deliver a reminder's ticks?
                }
            }

            public override string ToString()
                => $"[{Identity.ReminderName}, {Identity.GrainId}, {period}, {LogFormatter.PrintDate(firstTickTime)}, {ETag}, {LocalSequenceNumber}, {(timer == null ? "Not_ticking" : "Ticking")}]";
        }

        private readonly struct ReminderIdentity : IEquatable<ReminderIdentity>
        {
            public readonly GrainId GrainId;
            public readonly string ReminderName;

            public ReminderIdentity(GrainId grainId, string reminderName)
            {
                if (grainId.IsDefault)
                    throw new ArgumentNullException(nameof(grainId));

                if (string.IsNullOrWhiteSpace(reminderName))
                    throw new ArgumentException("The reminder name is either null or whitespace.", "reminderName");

                this.GrainId = grainId;
                this.ReminderName = reminderName;
            }

            public readonly bool Equals(ReminderIdentity other) => GrainId.Equals(other.GrainId) && ReminderName.Equals(other.ReminderName);

            public override readonly bool Equals(object other) => other is ReminderIdentity id && Equals(id);

            public override readonly int GetHashCode() => HashCode.Combine(GrainId, ReminderName);
        }
    }
}
