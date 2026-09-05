using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.CodeGeneration;
using Orleans.GrainReferences;
using Orleans.Hosting;
using Orleans.Internal;
using Orleans.Metadata;
using Orleans.Reminders;
using Orleans.Reminders.Diagnostics;
using Orleans.Runtime.ConsistentRing;
using Orleans.Runtime.Internal;
using Orleans.Runtime.MembershipService;
using Orleans.Runtime.Scheduler;

namespace Orleans.Runtime.ReminderService
{
    internal sealed partial class LocalReminderService : GrainService, IReminderService, ILifecycleParticipant<ISiloLifecycle>, IGrainServiceRangeChangeQueue
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
        private readonly IAsyncTimer listRefreshTimer; // timer that refreshes our list of reminders to reflect global reminder table
        private readonly GrainReferenceActivator _referenceActivator;
        private readonly GrainInterfaceType _grainInterfaceType;
        private readonly SiloStatusListenerManager _siloStatusListenerManager;
        private readonly TimeProvider _timeProvider;
        private readonly ReminderInstruments _reminderInstruments;
        private long localTableSequence;
        // The test barrier reads this state off-scheduler so it remains observable while the service is busy.
        private readonly object _reconciliationLock = new();
        private long reconciliationGeneration;
        private TaskCompletionSource reconciliationGenerationChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task reconciliationTask = Task.CompletedTask;
        private uint initialReadCallCount = 0;
        private Task? runTask;
        private readonly object _deliveryLock = new();
        private bool _isDeliveringReminders;
        private int _activeReminderDeliveries;
        private TaskCompletionSource? _deliveryQuiesced;

        public LocalReminderService(
            GrainReferenceActivator referenceActivator,
            GrainInterfaceTypeResolver interfaceTypeResolver,
            IReminderTable reminderTable,
            IAsyncTimerFactory asyncTimerFactory,
            IOptions<ReminderOptions> reminderOptions,
            IConsistentRingProvider ringProvider,
            SiloStatusListenerManager siloStatusListenerManager,
            [FromKeyedServices(ReminderTimeProviderNames.Reminders)] TimeProvider timeProvider,
            ReminderInstruments reminderInstruments,
            SystemTargetShared shared)
            : base(
                  SystemTargetGrainId.CreateGrainServiceGrainId(GrainInterfaceUtils.GetGrainClassTypeCode(typeof(IReminderService)), null!, shared.SiloAddress),
                  ringProvider,
                  shared)
        {
            _referenceActivator = referenceActivator;
            _grainInterfaceType = interfaceTypeResolver.GetGrainInterfaceType(typeof(IRemindable));
            _siloStatusListenerManager = siloStatusListenerManager;
            this.reminderOptions = reminderOptions.Value;
            this.reminderTable = reminderTable;
            _timeProvider = timeProvider;
            _reminderInstruments = reminderInstruments;
            _reminderInstruments.RegisterActiveRemindersObserve(() => localReminders.Count);
            startedTask = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            this.logger = shared.LoggerFactory.CreateLogger<LocalReminderService>();
            this.listRefreshTimer = asyncTimerFactory.Create(this.reminderOptions.RefreshReminderListPeriod, "ReminderService.ReminderListRefresher", _timeProvider);
            shared.ActivationDirectory.RecordNewTarget(this);
        }

        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle observer)
        {
            observer.Subscribe(
                nameof(LocalReminderService),
                ServiceLifecycleStage.RuntimeGrainServices,
                StartReminderTable,
                StopReminderTable);

            async Task StartReminderTable(CancellationToken ct)
            {
                try
                {
                    await this.QueueTask(() => StartReminderTableCoreAsync(ct));
                }
                catch (Exception exception)
                {
                    LogErrorActivatingReminderService(exception);
                    throw;
                }

                async Task StartReminderTableCoreAsync(CancellationToken cancellationToken)
                {
                    CheckRuntimeContext();

                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(this.reminderOptions.InitializationTimeout);

                    // Confirm that it can access the underlying store, as after this the ReminderService will load in the background, without the opportunity to prevent the Silo from starting
                    await reminderTable.StartAsync(cts.Token);
                }
            }

            async Task StopReminderTable(CancellationToken ct)
            {
                try
                {
                    await this.QueueTask(StopReminderServiceAndTable).WaitAsync(ct);
                }
                catch (Exception exception)
                {
                    LogErrorStoppingReminderService(exception);
                    throw;
                }

                async Task StopReminderServiceAndTable()
                {
                    await StopReminderService();
                    await reminderTable.StopAsync(ct);
                }
            }
        }

        public override Task Start() => Start(CancellationToken.None);

        public async Task Start(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckRuntimeContext();

            try
            {
                lock (_deliveryLock)
                {
                    if (_isDeliveringReminders)
                    {
                        return;
                    }

                    _isDeliveringReminders = true;
                }

                foreach (var reminderData in localReminders.Values)
                {
                    reminderData.TryStart();
                }

                await base.Start();
            }
            catch (Exception exception)
            {
                await StopReminderService();
                LogErrorStartingReminderService(exception);
                throw;
            }
        }

        public override Task Stop() => Stop(CancellationToken.None);

        public async Task Stop(CancellationToken cancellationToken)
        {
            CheckRuntimeContext();
            await StopDeliveringReminders().WaitAsync(cancellationToken);
        }

        private async Task StopDeliveringReminders()
        {
            Task? deliveryQuiescedTask = null;
            lock (_deliveryLock)
            {
                _isDeliveringReminders = false;
                if (_activeReminderDeliveries > 0)
                {
                    _deliveryQuiesced ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
                    deliveryQuiescedTask = _deliveryQuiesced.Task;
                }
            }

            if (deliveryQuiescedTask is not null)
            {
                await deliveryQuiescedTask;
            }

            // Stop all reminders.
            var tasks = new List<Task>(localReminders.Count);
            foreach (var reminderData in localReminders.Values)
            {
                tasks.Add(reminderData.StopAsync(ReminderEvents.LocalReminderStopReason.ServiceStopped));
            }

            await Task.WhenAll(tasks);
        }

        private async Task StopReminderService()
        {
            await StopDeliveringReminders();
            await base.Stop();

            listRefreshTimer.Dispose();
            if (this.runTask is { } task)
            {
                await task;
            }

            // For a graceful shutdown, also handover reminder responsibilities to new owner, and update the ReminderTable
            // currently, this is taken care of by periodically reading the reminder table
        }

        public async Task<IGrainReminder> RegisterOrUpdateReminder(GrainId grainId, string reminderName, TimeSpan dueTime, TimeSpan period)
            => await RegisterOrUpdateReminder(grainId, reminderName, dueTime, period, CancellationToken.None);

        public async Task<IGrainReminder> RegisterOrUpdateReminder(
            GrainId grainId,
            string reminderName,
            TimeSpan dueTime,
            TimeSpan period,
            CancellationToken cancellationToken)
        {
            CheckRuntimeContext();

            var entry = new ReminderEntry
            {
                GrainId = grainId,
                ReminderName = reminderName,
                StartAt = _timeProvider.GetUtcNow().UtcDateTime.Add(dueTime),
                Period = period,
            };

            LogDebugRegisterOrUpdateReminder(entry);
            cancellationToken.ThrowIfCancellationRequested();
            await DoResponsibilitySanityCheck(grainId, "RegisterReminder").WaitAsync(cancellationToken);
            string? newEtag = await reminderTable.UpsertRow(entry, cancellationToken);

            if (newEtag != null)
            {
                entry.ETag = newEtag;
                // A request can arrive on a stale owner. Persist it here, but let the current owner load it.
                if (RingRange.InRange(grainId))
                {
                    ReconcileLocalReminder(entry, _timeProvider.GetUtcNow().UtcDateTime);
                }

                LogDebugRegisterReminder(entry, localTableSequence);

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
            => await UnregisterReminder(reminder, CancellationToken.None);

        public async Task UnregisterReminder(IGrainReminder reminder, CancellationToken cancellationToken)
        {
            CheckRuntimeContext();

            var remData = (ReminderData)reminder;
            LogDebugUnregisterReminder(reminder, localTableSequence);

            var grainId = remData.GrainId;
            string reminderName = remData.ReminderName;
            string eTag = remData.ETag;

            cancellationToken.ThrowIfCancellationRequested();
            await DoResponsibilitySanityCheck(grainId, "RemoveReminder").WaitAsync(cancellationToken);

            // it may happen that we dont have this reminder locally ... even then, we attempt to remove the reminder from the reminder
            // table ... the periodic mechanism will stop this reminder at any silo's LocalReminderService that might have this reminder locally

            // remove from persistent/memory store
            var success = await reminderTable.RemoveRow(grainId, reminderName, eTag, cancellationToken);
            if (!success)
            {
                success = await IsReminderAlreadyRemoved(grainId, reminderName, reminder, cancellationToken);
            }

            if (success)
            {
                var key = new ReminderIdentity(grainId, reminderName);
                if (localReminders.TryGetValue(key, out var localRem))
                {
                    RequestLocalReminderRemoval(key, localRem, ReminderEvents.LocalReminderStopReason.Unregistered);
                    LogStoppedReminder(reminder);
                    if (logger.IsEnabled(LogLevel.Trace)) PrintReminders($"After removing {reminder}.");
                }
                else
                {
                    AddLocalReminderTombstone(key, ReminderEvents.LocalReminderStopReason.Unregistered);
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

        private async Task<bool> IsReminderAlreadyRemoved(GrainId grainId, string reminderName, IGrainReminder reminder, CancellationToken cancellationToken)
        {
            if (await reminderTable.ReadRow(grainId, reminderName, cancellationToken) is not null)
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
            => await GetReminder(grainId, reminderName, CancellationToken.None);

        public async Task<IGrainReminder?> GetReminder(GrainId grainId, string reminderName, CancellationToken cancellationToken)
        {
            LogDebugGetReminder(grainId, reminderName);
            ReminderEntry? entry = await reminderTable.ReadRow(grainId, reminderName, cancellationToken);
            return entry?.ToIGrainReminder();
        }

        async Task<IGrainReminder?> IReminderService.GetReminder(GrainId grainId, string reminderName)
            => await GetReminder(grainId, reminderName);

        public async Task<List<IGrainReminder>> GetReminders(GrainId grainId)
            => await GetReminders(grainId, CancellationToken.None);

        public async Task<List<IGrainReminder>> GetReminders(GrainId grainId, CancellationToken cancellationToken)
        {
            LogDebugGetReminders(grainId);
            var tableData = await reminderTable.ReadRows(grainId, cancellationToken);
            return tableData.Reminders.Select(entry => entry.ToIGrainReminder()).ToList();
        }

        /// <summary>
        /// Attempt to retrieve reminders from the global reminder table
        /// </summary>
        private Task ReadAndUpdateReminders()
        {
            CheckRuntimeContext();

            if (StoppedCancellationTokenSource.IsCancellationRequested) return Task.CompletedTask;

            var tasks = new List<Task>();
            RemoveOutOfRangeReminders(tasks);

            // Refreshes use even sequence values. Local writes use the following odd value, so they can supersede
            // this snapshot without advancing the refresh generation. A newer refresh advances by two and causes
            // all older refresh results to be discarded.
            var cachedSequence = localTableSequence += 2;
            var rangeSerialNumberCopy = RangeSerialNumber;
            LogTraceRingRange(RingRange, RangeSerialNumber, localReminders.Count);
            foreach (var range in RangeFactory.GetSubRanges(RingRange))
            {
                tasks.Add(ReadAndReconcileRange(range, rangeSerialNumberCopy, cachedSequence));
            }
            var task = Task.WhenAll(tasks);
            if (logger.IsEnabled(LogLevel.Trace)) task.ContinueWith(_ => PrintReminders(), TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously);
            return task;
        }

        internal Task<Task> TestOnlyStartRefresh() => QueueRefresh();

        internal Task TestOnlyRefresh() => QueueRefresh().Unwrap();

        internal bool TestOnlyIsStarted => Status == GrainServiceStatus.Started;

        private Task<Task> QueueRefresh()
        {
            try
            {
                return Task.FromResult(QueueTrackedReconciliation(ReadAndUpdateReminders));
            }
            catch (Exception exception)
            {
                return Task.FromException<Task>(exception);
            }
        }

        internal Task TestOnlyWaitForSiloStatusListeners(CancellationToken cancellationToken)
            => _siloStatusListenerManager.TestOnlyWaitForCurrentMembershipVersion(cancellationToken);

        internal (MembershipVersion Current, MembershipVersion Processed) TestOnlyGetMembershipVersions()
            => _siloStatusListenerManager.TestOnlyGetMembershipVersions();

        internal string TestOnlyDescribeTopologyState()
        {
            lock (_reconciliationLock)
            {
                return $"silo={Silo}, serviceStatus={Status}, {_siloStatusListenerManager.TestOnlyDescribeMembershipState()}, "
                    + $"ringRange={RingRange}, reconciliationGeneration={reconciliationGeneration}, "
                    + $"reconciliationCompleted={reconciliationTask.IsCompleted}";
            }
        }

        private void RemoveOutOfRangeReminders(List<Task> removedReminderTasks)
        {
            CheckRuntimeContext();

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
            => TrackReconciliation(() => ApplyRangeChange(oldRange, newRange, increased));

        void IGrainServiceRangeChangeQueue.QueueRangeChange(IRingRange oldRange, IRingRange newRange, bool increased)
            => QueueTrackedRangeChange(oldRange, newRange, increased).Ignore();

        private Task QueueTrackedRangeChange(IRingRange oldRange, IRingRange newRange, bool increased)
            => QueueTrackedReconciliation(() => ApplyRangeChange(oldRange, newRange, increased));

        private Task QueueTrackedReconciliation(Func<Task> reconciliation)
            => TrackReconciliation(() => this.QueueTask(reconciliation));

        private Task ApplyRangeChange(IRingRange oldRange, IRingRange newRange, bool increased)
        {
            CheckRuntimeContext();

            _ = base.OnRangeChange(oldRange, newRange, increased);
            var status = Status;
            if (status == GrainServiceStatus.Started)
            {
                return ReadAndUpdateReminders();
            }

            LogIgnoringRangeChange(status);
            return Task.CompletedTask;
        }

        private Task TrackReconciliation(Func<Task> reconciliation)
        {
            var reconciliationTaskSource = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource? previousGenerationChanged = null;
            try
            {
                lock (_reconciliationLock)
                {
                    previousGenerationChanged = reconciliationGenerationChanged;
                    reconciliationGenerationChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    reconciliationGeneration++;
                    reconciliationTask = reconciliationTaskSource.Task.Unwrap();

                    try
                    {
                        var task = reconciliation();
                        reconciliationTaskSource.SetResult(task);
                        return task;
                    }
                    catch (Exception exception)
                    {
                        reconciliationTaskSource.SetException(exception);
                        throw;
                    }
                }
            }
            finally
            {
                previousGenerationChanged?.TrySetResult();
            }
        }

        internal async Task TestOnlyWaitForRangeChangeReconciliation(CancellationToken cancellationToken)
        {
            while (true)
            {
                // A newer tracked refresh supersedes older results through localTableSequence. Follow
                // reconciliation generations so a stalled obsolete read does not block the current one.
                long observedGeneration;
                Task observedTask;
                Task observedGenerationChanged;
                lock (_reconciliationLock)
                {
                    observedGeneration = reconciliationGeneration;
                    observedTask = reconciliationTask;
                    observedGenerationChanged = reconciliationGenerationChanged.Task;
                }

                await Task.WhenAny(observedTask, observedGenerationChanged).WaitAsync(cancellationToken);

                lock (_reconciliationLock)
                {
                    if (observedGeneration != reconciliationGeneration)
                    {
                        continue;
                    }

                    // The generation-change task can only complete after the generation advances, so the
                    // observed reconciliation is complete and its outcome can be read without blocking.
                    observedTask.GetAwaiter().GetResult();
                    return;
                }
            }
        }

        internal Task TestOnlyChangeRange(IRingRange oldRange, IRingRange newRange, bool increased)
            => QueueTrackedRangeChange(oldRange, newRange, increased);

        private async Task RunAsync()
        {
            var initialRefreshStagger = reminderOptions.RefreshReminderListPeriod < InitialReadRetryPeriod
                ? reminderOptions.RefreshReminderListPeriod
                : InitialReadRetryPeriod;
            TimeSpan? overrideDelay = RandomTimeSpan.Next(initialRefreshStagger);
            int consecutiveFailures = 0;
            while (await listRefreshTimer.NextTick(overrideDelay))
            {
                try
                {
                    overrideDelay = null;
                    await QueueTrackedReconciliation(ProcessRefreshTick);

                    consecutiveFailures = 0;
                }
                catch (Exception exception)
                {
                    LogWarningReadingReminders(exception);

                    overrideDelay = BackoffComputation.ComputeBackoffDelay(
                        ++consecutiveFailures,
                        baseMin: TimeSpan.FromSeconds(10),
                        baseMax: TimeSpan.FromSeconds(20),
                        cap: TimeSpan.FromSeconds(80));
                }
            }

            async Task ProcessRefreshTick()
            {
                CheckRuntimeContext();

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
                        break;
                }
            }
        }

        protected override async Task StartInBackground()
        {
            CheckRuntimeContext();

            // Observe the ring before loading so readiness covers the current range, including changes during the read.
            SubscribeToRangeChangeEvents();
            await this.QueueAction(static _ => { }, state: 0);
            await DoInitialReadAndUpdateReminders();
            using var suppressExecutionContext = new ExecutionContextSuppressor();
            this.runTask = Task.Run(RunAsync);
        }

        private async Task DoInitialReadAndUpdateReminders()
        {
            CheckRuntimeContext();

            try
            {
                if (StoppedCancellationTokenSource.IsCancellationRequested) return;

                initialReadCallCount++;
                while (true)
                {
                    var rangeSerialNumber = RangeSerialNumber;
                    await this.ReadAndUpdateReminders();
                    await this.QueueAction(static _ => { }, state: 0);
                    if (rangeSerialNumber == RangeSerialNumber)
                    {
                        break;
                    }
                }

                Status = GrainServiceStatus.Started;
                startedTask.TrySetResult(true);
                ReminderEvents.EmitReminderServiceStarted(Silo);
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

        private async Task ReadAndReconcileRange(ISingleRange range, int rangeSerialNumberCopy, long cachedSequence)
        {
            CheckRuntimeContext();

            LogDebugReadingRows(range);

            try
            {
                // The read sequence was captured before any range read yielded. Local mutations which run while
                // storage is reading receive a later sequence and therefore win when this snapshot returns.
                ReminderTableData? table = await reminderTable.ReadRows(
                    range.Begin,
                    range.End,
                    StoppedCancellationTokenSource.Token); // get all reminders, even the ones we already have

                if (cachedSequence < localTableSequence)
                {
                    // A newer refresh has started, so this result can no longer retire tombstones or change schedules.
                    return;
                }

                if (rangeSerialNumberCopy < RangeSerialNumber)
                {
                    LogDebugRangeChangedWhileFromTable(RangeSerialNumber, rangeSerialNumberCopy);
                    return;
                }

                if (StoppedCancellationTokenSource.IsCancellationRequested) return;

                // Providers built against older Orleans versions can still return null.
                if (table is null) return;

                // Begin with every loaded reminder in this range, then remove identities as storage returns them.
                // Anything left afterward has been deleted from storage and must also be removed locally.
                var remindersNotInTable = new HashSet<ReminderIdentity>();
                foreach (var key in localReminders.Keys)
                {
                    if (range.InRange(key.GrainId))
                    {
                        remindersNotInTable.Add(key);
                    }
                }

                LogDebugReadRemindersFromTable(range, table.Reminders.Count, localTableSequence, cachedSequence);
                var tasks = new List<Task>();
                // Use one timestamp for the entire snapshot so every row is evaluated against the same loading window.
                var now = _timeProvider.GetUtcNow().UtcDateTime;
                foreach (var entry in table.Reminders)
                {
                    var key = new ReminderIdentity(entry.GrainId, entry.ReminderName);
                    remindersNotInTable.Remove(key);
                    ReconcileTableEntry(entry, cachedSequence, now, tasks);
                }

                int remindersCountBeforeRemove = localReminders.Count;

                // A newer local update wins over this snapshot. Otherwise, reminders which storage did not
                // return are no longer ours to schedule.
                foreach (var key in remindersNotInTable)
                {
                    if (!localReminders.TryGetValue(key, out var reminder))
                    {
                        continue;
                    }

                    if (cachedSequence <= reminder.LocalSequenceNumber)
                    {
                        LogTraceNotInTableInLocalNewer(reminder);
                    }
                    else
                    {
                        LogTraceNotInTableInLocalOld(reminder);
                        tasks.Add(
                            RemoveLocalReminder(
                                key,
                                reminder,
                                ReminderEvents.LocalReminderStopReason.RemovedFromTable,
                                cachedSequence));
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

        private void ReconcileTableEntry(ReminderEntry entry, long tableSequence, DateTime now, List<Task> stopTasks)
        {
            var key = new ReminderIdentity(entry.GrainId, entry.ReminderName);
            var nextTick = CalculateNextTickTime(entry, now);
            var isWithinLoadingWindow = nextTick <= now.AddClamped(reminderOptions.ReminderLoadingWindow);
            if (!localReminders.TryGetValue(key, out var localReminder))
            {
                // Distant reminders remain exclusively in storage until a later refresh brings their next tick
                // into the loading window.
                if (isWithinLoadingWindow)
                {
                    LogTraceInTableNotInLocal(entry);
                    AddOrUpdateLocalReminder(entry, tableSequence);
                }

                return;
            }

            var state = localReminder.State;
            // A direct registration or update which completed after this read began is newer than the table
            // snapshot, so leave its local schedule unchanged.
            if (tableSequence <= localReminder.LocalSequenceNumber)
            {
                if (state is LocalReminderState.Running)
                {
                    LogTraceInTableInLocalNewerTicking(localReminder);
                }
                else
                {
                    LogTraceInTableInLocalNewerNotTicking(localReminder);
                }

                return;
            }

            // Retain the marker for an unchanged occurrence which this owner fired at the same timestamp.
            if (state is LocalReminderState.Tombstone
                && localReminder.ShouldRetainFiredOccurrenceTombstone(entry, nextTick))
            {
                localReminder.LocalSequenceNumber = tableSequence;
                return;
            }

            if (!isWithinLoadingWindow)
            {
                LogTraceRemovingReminder(localReminder);
                stopTasks.Add(
                    RemoveLocalReminder(
                        key,
                        localReminder,
                        ReminderEvents.LocalReminderStopReason.OutsideLoadingWindow,
                        tableSequence));
                return;
            }

            if (state is LocalReminderState.Tombstone)
            {
                LogTraceInTableInLocalOldNotTicking(localReminder);
                AddOrUpdateLocalReminder(entry, tableSequence);
                return;
            }

            if (state is not LocalReminderState.Running)
            {
                LogTraceInTableInLocalOldNotTicking(localReminder);
                return;
            }

            LogTraceInTableInLocalOldTicking(localReminder);
            if (!StringComparer.Ordinal.Equals(localReminder.Entry.ETag, entry.ETag))
            {
                LogTraceLocalReminderNeedsUpdate(localReminder);
                AddOrUpdateLocalReminder(entry, tableSequence);
            }
        }

        private void ReconcileLocalReminder(ReminderEntry entry, DateTime now)
        {
            if (IsReminderWithinLoadingWindow(entry, now, reminderOptions.ReminderLoadingWindow))
            {
                AddOrUpdateLocalReminder(entry);
            }
            else
            {
                // The updated schedule is durable, but its next tick is too distant to justify retaining a
                // local task. Keep a stopped sequence-bearing entry until a newer refresh observes this write.
                AddLocalReminderTombstone(entry, ReminderEvents.LocalReminderStopReason.OutsideLoadingWindow);
            }
        }

        private void AddOrUpdateLocalReminder(ReminderEntry entry)
            => AddOrUpdateLocalReminder(entry, GetLocalMutationSequence());

        private void AddOrUpdateLocalReminder(ReminderEntry entry, long sequence)
        {
            CheckRuntimeContext();

            var key = new ReminderIdentity(entry.GrainId, entry.ReminderName);
            LocalReminderData reminderData;
            if (localReminders.TryGetValue(key, out var existing))
            {
                if (existing.State is LocalReminderState.Tombstone)
                {
                    reminderData = LocalReminderData.CreateRunnable(entry, this, sequence);
                    localReminders[key] = reminderData;
                    LogDebugStartedReminder(entry);
                }
                else
                {
                    reminderData = existing;
                    reminderData.LocalSequenceNumber = sequence;
                    reminderData.Update(entry);
                    LogDebugUpdatedReminder(entry);
                }
            }
            else
            {
                reminderData = LocalReminderData.CreateRunnable(entry, this, sequence);
                localReminders.Add(key, reminderData);

                LogDebugStartedReminder(entry);
            }

            lock (_deliveryLock)
            {
                if (!_isDeliveringReminders)
                {
                    return;
                }
            }

            reminderData.TryStart();
        }

        private void AddLocalReminderTombstone(ReminderEntry entry, ReminderEvents.LocalReminderStopReason reason)
        {
            var key = new ReminderIdentity(entry.GrainId, entry.ReminderName);
            if (localReminders.TryGetValue(key, out var existing) && existing.State is not LocalReminderState.Tombstone)
            {
                existing.Update(entry);
                RequestLocalReminderRemoval(key, existing, reason);
                return;
            }

            localReminders[key] = LocalReminderData.CreateTombstone(entry, this, reason, GetLocalMutationSequence());
            LogDebugStoppingReminder(entry, reason);
        }

        private void AddLocalReminderTombstone(ReminderIdentity key, ReminderEvents.LocalReminderStopReason reason)
        {
            var sequence = GetLocalMutationSequence();
            localReminders[key] = LocalReminderData.CreateTombstone(key, this, reason, sequence);
        }

        private void RequestLocalReminderRemoval(
            ReminderIdentity key,
            LocalReminderData reminder,
            ReminderEvents.LocalReminderStopReason reason)
        {
            if (!localReminders.TryGetValue(key, out var current) || !ReferenceEquals(current, reminder))
            {
                return;
            }

            var stopTask = reminder.StopAsync(reason, GetLocalMutationSequence());
            ObserveLocalReminderStop(stopTask, key.GrainId, key.ReminderName);
        }

        private long GetLocalMutationSequence() => localTableSequence + 1;

        private Task RemoveLocalReminder(
            ReminderIdentity key,
            LocalReminderData reminder,
            ReminderEvents.LocalReminderStopReason reason,
            long sequence)
        {
            if (!localReminders.TryGetValue(key, out var current) || !ReferenceEquals(current, reminder))
            {
                return Task.CompletedTask;
            }

            var stopTask = reminder.StopAsync(reason, sequence);
            localReminders.Remove(key);
            return stopTask;
        }

        private bool TryBeginSingleReminderDelivery()
        {
            lock (_deliveryLock)
            {
                if (!_isDeliveringReminders)
                {
                    return false;
                }

                ++_activeReminderDeliveries;
                return true;
            }
        }

        private void CompleteSingleReminderDelivery()
        {
            TaskCompletionSource? quiesced = null;
            lock (_deliveryLock)
            {
                --_activeReminderDeliveries;
                if (_activeReminderDeliveries == 0)
                {
                    quiesced = _deliveryQuiesced;
                    _deliveryQuiesced = null;
                }
            }

            quiesced?.SetResult();
        }

        private Task DoResponsibilitySanityCheck(GrainId grainId, string debugInfo)
        {
            CheckRuntimeContext();

            switch (Status)
            {
                case GrainServiceStatus.Booting:
                    // if service didn't finish the initial load, it could still be loading normally or it might have already
                    // failed a few attempts and callers should not be hold waiting for it to complete
                    var task = this.startedTask.Task;
                    if (task.IsCompleted)
                    {
                        // Propagate any initial-load failure before checking the range.
                        task.GetAwaiter().GetResult();
                        CheckRange();
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
                    CheckRange();
                    break;
                case GrainServiceStatus.Stopped:
                    return Task.CompletedTask;
                default:
                    throw new InvalidOperationException("status");
            }

            return Task.CompletedTask;

            void CheckRange()
            {
                CheckRuntimeContext();

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

        internal static DateTime CalculateNextTickTime(ReminderEntry entry, DateTime now)
        {
            ArgumentNullException.ThrowIfNull(entry);
            Debug.Assert(now.Kind == DateTimeKind.Utc);
            if (entry.Period <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(entry), entry.Period, "Reminder period must be greater than zero.");
            }

            // Reminder timestamps represent UTC even if a storage provider loses DateTimeKind.
            var startAt = DateTime.SpecifyKind(entry.StartAt, DateTimeKind.Utc);
            if (now <= startAt)
            {
                return startAt;
            }

            var sinceFirstTick = now.Ticks - startAt.Ticks;
            var sinceLastTick = sinceFirstTick % entry.Period.Ticks;
            if (sinceLastTick == 0)
            {
                return now;
            }

            return now.AddClamped(TimeSpan.FromTicks(entry.Period.Ticks - sinceLastTick));
        }

        internal static bool IsReminderWithinLoadingWindow(ReminderEntry entry, DateTime now, TimeSpan loadingWindow)
        {
            Debug.Assert(now.Kind == DateTimeKind.Utc);
            if (loadingWindow <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(loadingWindow), loadingWindow, "The reminder loading window must be greater than zero.");
            }

            return CalculateNextTickTime(entry, now) <= now.AddClamped(loadingWindow);
        }

        internal static bool ShouldRetainFiredOccurrenceTombstone(
            ReminderEntry localEntry,
            ReminderEntry tableEntry,
            DateTime? lastFiredTickTime,
            DateTime nextTick)
            => lastFiredTickTime == nextTick && StringComparer.Ordinal.Equals(localEntry.ETag, tableEntry.ETag);

        internal static DateTime CalculateFollowingTickTime(ReminderEntry entry, DateTime previousTickTime, DateTime now)
        {
            Debug.Assert(previousTickTime.Kind == DateTimeKind.Utc);
            Debug.Assert(now.Kind == DateTimeKind.Utc);
            var nextTick = CalculateNextTickTime(entry, now);
            return nextTick > previousTickTime ? nextTick : previousTickTime.AddClamped(entry.Period);
        }

        private bool TryEvictLocalReminder(LocalReminderData reminder, long scheduleVersion, DateTime? lastFiredTickTime)
        {
            CheckRuntimeContext();

            var entry = reminder.Entry;
            var key = new ReminderIdentity(entry.GrainId, entry.ReminderName);
            // Only the local instance and schedule version which made the eviction decision may remove itself.
            // A concurrent update leaves the reminder in place so its run loop can observe the new schedule.
            if (!localReminders.TryGetValue(key, out var current) || !ReferenceEquals(current, reminder))
            {
                return true;
            }

            var sequence = GetLocalMutationSequence();
            if (!reminder.TryStopOutsideLoadingWindow(scheduleVersion, sequence, lastFiredTickTime))
            {
                return false;
            }

            // Keep the stopped object as a tombstone until a newer table read observes this scheduling decision.
            return true;
        }

        private enum LocalReminderState
        {
            Stopped,
            Running,
            Tombstone,
        }

        private sealed class LocalReminderData
        {
            private readonly LocalReminderService _shared;
            private readonly CancellationTokenSource _stopCancellation = new();
#if NET10_0_OR_GREATER
            private readonly System.Threading.Lock _lock = new();
#else
            private readonly object _lock = new();
#endif
            private readonly ReminderIdentity _identity;
            private ReminderEntry? _entry;
            private DateTime? _lastFiredTickTime;
            private CancellationTokenSource _scheduleChangedCancellation = new();
            private long _scheduleVersion;

            private int _stopReason;
            private long _localSequenceNumber;
            private Task? _runTask;

            private LocalReminderData(
                ReminderIdentity identity,
                ReminderEntry? entry,
                LocalReminderService reminderService,
                ReminderEvents.LocalReminderStopReason reason,
                long localSequenceNumber)
            {
                _shared = reminderService;
                _entry = entry;
                _identity = identity;
                _localSequenceNumber = localSequenceNumber;
                _stopReason = (int)reason;
            }

            public static LocalReminderData CreateRunnable(
                ReminderEntry entry,
                LocalReminderService reminderService,
                long localSequenceNumber)
                => new(
                    new ReminderIdentity(entry.GrainId, entry.ReminderName),
                    entry,
                    reminderService,
                    ReminderEvents.LocalReminderStopReason.Unknown,
                    localSequenceNumber);

            public static LocalReminderData CreateTombstone(
                ReminderEntry entry,
                LocalReminderService reminderService,
                ReminderEvents.LocalReminderStopReason reason,
                long localSequenceNumber)
                => new(
                    new ReminderIdentity(entry.GrainId, entry.ReminderName),
                    entry,
                    reminderService,
                    reason,
                    localSequenceNumber);

            public static LocalReminderData CreateTombstone(
                ReminderIdentity identity,
                LocalReminderService reminderService,
                ReminderEvents.LocalReminderStopReason reason,
                long localSequenceNumber)
                => new(identity, entry: null, reminderService, reason, localSequenceNumber);

            public ReminderEntry Entry
            {
                get
                {
                    lock (_lock)
                    {
                        return _entry ?? throw new InvalidOperationException($"Reminder {_identity} is a tombstone.");
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

            public LocalReminderState State
            {
                get
                {
                    lock (_lock)
                    {
                        if (_stopReason != (int)ReminderEvents.LocalReminderStopReason.Unknown)
                        {
                            return LocalReminderState.Tombstone;
                        }

                        return _runTask is Task task && !task.IsCompleted
                            ? LocalReminderState.Running
                            : LocalReminderState.Stopped;
                    }
                }
            }

            public bool ShouldRetainFiredOccurrenceTombstone(ReminderEntry entry, DateTime nextTick)
            {
                lock (_lock)
                {
                    return _entry is { } localEntry
                        && LocalReminderService.ShouldRetainFiredOccurrenceTombstone(
                            localEntry,
                            entry,
                            _lastFiredTickTime,
                            nextTick);
                }
            }

            public bool TryStart()
            {
                GrainId grainId;
                string reminderName;
                lock (_lock)
                {
                    if (_runTask is not null || _stopReason is not (int)ReminderEvents.LocalReminderStopReason.Unknown)
                    {
                        return false;
                    }

                    var entry = _entry ?? throw new InvalidOperationException($"Reminder {_identity} is a tombstone.");
                    grainId = entry.GrainId;
                    reminderName = entry.ReminderName;
                    using var suppressExecutionContext = new ExecutionContextSuppressor();
                    _runTask = RunAsync(grainId, reminderName);
                }

                ReminderEvents.EmitLocalReminderStarted(grainId, reminderName, this, _shared.Silo);
                return true;
            }

            public void Update(ReminderEntry entry)
            {
                ArgumentNullException.ThrowIfNull(entry);

                long scheduleVersion;
                CancellationTokenSource scheduleChangedCancellation;
                lock (_lock)
                {
                    if (_identity.GrainId != entry.GrainId || !StringComparer.Ordinal.Equals(_identity.ReminderName, entry.ReminderName))
                    {
                        throw new InvalidOperationException($"Cannot update reminder {_identity} with {entry} because the reminder identity changed.");
                    }

                    _entry = entry;
                    scheduleVersion = ++_scheduleVersion;
                    scheduleChangedCancellation = _scheduleChangedCancellation;
                    _scheduleChangedCancellation = new();
                }

                ReminderEvents.EmitLocalReminderScheduleChanged(entry.GrainId, entry.ReminderName, this, scheduleVersion, _shared.Silo);
                scheduleChangedCancellation.Cancel();
                scheduleChangedCancellation.Dispose();
            }

            public Task StopAsync(ReminderEvents.LocalReminderStopReason reason, long? localSequenceNumber = null)
            {
                ReminderEntry? entry;
                CancellationTokenSource scheduleChangedCancellation;
                Task? runTask;
                lock (_lock)
                {
                    entry = _entry;
                    if (localSequenceNumber is { } sequence)
                    {
                        _localSequenceNumber = sequence;
                    }

                    if (_stopReason == (int)ReminderEvents.LocalReminderStopReason.Unknown)
                    {
                        _stopReason = (int)reason;
                    }

                    scheduleChangedCancellation = _scheduleChangedCancellation;
                    runTask = _runTask;
                }

                if (entry is not null)
                {
                    _shared.LogDebugStoppingReminder(entry, reason);
                }
                _stopCancellation.Cancel();
                scheduleChangedCancellation.Cancel();
                return runTask ?? Task.CompletedTask;
            }

            public bool TryStopOutsideLoadingWindow(
                long scheduleVersion,
                long localSequenceNumber,
                DateTime? lastFiredTickTime)
            {
                ReminderEntry entry;
                CancellationTokenSource scheduleChangedCancellation;
                lock (_lock)
                {
                    if (_stopReason != (int)ReminderEvents.LocalReminderStopReason.Unknown || _scheduleVersion != scheduleVersion)
                    {
                        return false;
                    }

                    _localSequenceNumber = localSequenceNumber;
                    _lastFiredTickTime = lastFiredTickTime;
                    _stopReason = (int)ReminderEvents.LocalReminderStopReason.OutsideLoadingWindow;
                    entry = _entry ?? throw new InvalidOperationException($"Reminder {_identity} is a tombstone.");
                    scheduleChangedCancellation = _scheduleChangedCancellation;
                }

                _shared.LogDebugStoppingReminder(entry, ReminderEvents.LocalReminderStopReason.OutsideLoadingWindow);
                _stopCancellation.Cancel();
                scheduleChangedCancellation.Cancel();
                return true;
            }

            private async Task RunAsync(GrainId grainId, string reminderName)
            {
                await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.ForceYielding);

                DateTime? previousTickTime = null;
                long previousScheduleVersion = -1;
                try
                {
                    while (await WaitForNextTick(previousTickTime, previousScheduleVersion) is { } scheduledTick)
                    {
                        var entry = PrepareTick(scheduledTick.ScheduleVersion);
                        if (entry is null || !_shared.TryBeginSingleReminderDelivery())
                        {
                            continue;
                        }

                        try
                        {
                            var before = _shared._timeProvider.GetUtcNow().UtcDateTime;
                            var status = new TickStatus(entry.StartAt, entry.Period, before);

                            LogTraceTriggeringTick(_shared.logger, this, status, before);
                            ReminderEvents.EmitTickFiring(entry.GrainId, entry.ReminderName, status, _shared.Silo);
                            if (_shared._reminderInstruments.TardinessSecondsEnabled)
                            {
                                var tardiness = CalculateTardiness(status);
                                _shared._reminderInstruments.OnTardiness(tardiness);
                            }

                            try
                            {
                                var grainRef = _shared.GetGrain(entry.GrainId);
                                await grainRef.ReceiveReminder(entry.ReminderName, status, _shared.StoppedCancellationTokenSource.Token);

                                if (_shared.logger.IsEnabled(LogLevel.Trace))
                                {
                                    var after = _shared._timeProvider.GetUtcNow().UtcDateTime;
                                    var elapsed = after - before;
                                    var nextTick = CalculateFollowingTickTime(entry, scheduledTick.TickTime, after);
                                    LogTraceTickTriggered(_shared.logger, this, elapsed.TotalSeconds, nextTick);
                                }

                                ReminderEvents.EmitTickCompleted(entry.GrainId, entry.ReminderName, status, _shared.Silo);
                                _shared._reminderInstruments.OnTickDelivered();
                            }
                            catch (Exception exc)
                            {
                                var after = _shared._timeProvider.GetUtcNow().UtcDateTime;
                                var nextTick = CalculateFollowingTickTime(entry, scheduledTick.TickTime, after);
                                LogErrorDeliveringReminderTick(_shared.logger, this, nextTick, exc);
                                ReminderEvents.EmitTickFailed(entry.GrainId, entry.ReminderName, status, exc, _shared.Silo);

                                // What to do with repeated failures to deliver a reminder's ticks?
                            }
                        }
                        catch (Exception exception)
                        {
                            LogWarningFiringReminder(_shared.logger, entry.ReminderName, entry.GrainId, exception);
                        }
                        finally
                        {
                            _shared.CompleteSingleReminderDelivery();
                        }

                        previousTickTime = scheduledTick.TickTime;
                        previousScheduleVersion = scheduledTick.ScheduleVersion;
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

            private async Task<ScheduledTick?> WaitForNextTick(DateTime? previousTickTime, long previousScheduleVersion)
            {
                while (true)
                {
                    ReminderEntry entry;
                    CancellationToken scheduleChangedToken;
                    GrainId grainId;
                    string reminderName;
                    long scheduleVersion;
                    lock (_lock)
                    {
                        if (_stopReason != (int)ReminderEvents.LocalReminderStopReason.Unknown)
                        {
                            return null;
                        }

                        entry = _entry ?? throw new InvalidOperationException($"Reminder {_identity} is a tombstone.");
                        scheduleChangedToken = _scheduleChangedCancellation.Token;
                        grainId = entry.GrainId;
                        reminderName = entry.ReminderName;
                        scheduleVersion = _scheduleVersion;
                    }

                    var now = _shared._timeProvider.GetUtcNow().UtcDateTime;
                    // Anchor every tick to the persisted StartAt + N * Period cadence. If delivery ran long,
                    // skip missed occurrences instead of drifting the schedule from delivery completion.
                    var tickTime = previousTickTime is { } previous && previousScheduleVersion == scheduleVersion
                        ? CalculateFollowingTickTime(entry, previous, now)
                        : CalculateNextTickTime(entry, now);

                    // Once the next tick is distant, discard the local task. The reminder remains durable and
                    // a future table refresh will load it again as it approaches the window.
                    if (tickTime > now.AddClamped(_shared.reminderOptions.ReminderLoadingWindow))
                    {
                        if (_shared.TryEvictLocalReminder(this, scheduleVersion, previousTickTime))
                        {
                            return null;
                        }

                        continue;
                    }

                    using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(_stopCancellation.Token, scheduleChangedToken);
                    try
                    {
                        // A tick due at or before now gets a minimal asynchronous delay to avoid a tight loop.
                        var waitUntil = tickTime <= now ? now.AddClamped(MinimumReminderDueTime) : tickTime;
                        // Reminder timestamps represent UTC even if a storage provider loses DateTimeKind.
                        var delayTask = _shared._timeProvider.DelayUntilAsync(
                            new DateTimeOffset(waitUntil.Ticks, TimeSpan.Zero),
                            waitCancellation.Token);
                        if (!scheduleChangedToken.IsCancellationRequested && !_stopCancellation.IsCancellationRequested)
                        {
                            ReminderEvents.EmitLocalReminderTickWaitArmed(grainId, reminderName, this, scheduleVersion, _shared.Silo);
                        }

                        await delayTask;
                        lock (_lock)
                        {
                            if (_stopReason != (int)ReminderEvents.LocalReminderStopReason.Unknown)
                            {
                                return null;
                            }

                            if (_scheduleVersion != scheduleVersion)
                            {
                                continue;
                            }

                            return new ScheduledTick(scheduleVersion, tickTime);
                        }
                    }
                    catch (OperationCanceledException) when (_stopCancellation.IsCancellationRequested)
                    {
                        return null;
                    }
                    catch (OperationCanceledException) when (scheduleChangedToken.IsCancellationRequested)
                    {
                        continue;
                    }
                }
            }

            private ReminderEntry? PrepareTick(long scheduleVersion)
            {
                lock (_lock)
                {
                    if (_stopReason != (int)ReminderEvents.LocalReminderStopReason.Unknown)
                    {
                        return null;
                    }

                    if (_scheduleVersion != scheduleVersion)
                    {
                        return null;
                    }

                    return _entry;
                }
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
                    return _entry is { } entry
                        ? $"[{entry.ReminderName}, {entry.GrainId}, {entry.Period}, {LogFormatter.PrintDate(entry.StartAt)}, {entry.ETag}, {_localSequenceNumber}, {(isRunning ? "Ticking" : "Stopped")}]"
                        : $"[{_identity.ReminderName}, {_identity.GrainId}, tombstone, {_localSequenceNumber}]";
                }
            }

            private readonly record struct ScheduledTick(long ScheduleVersion, DateTime TickTime);
        }

        private readonly struct ReminderIdentity(GrainId grainId, string reminderName) : IEquatable<ReminderIdentity>
        {
            public readonly GrainId GrainId = grainId;
            public readonly string ReminderName = reminderName;

            public readonly bool Equals(ReminderIdentity other) => GrainId.Equals(other.GrainId) && ReminderName.Equals(other.ReminderName, StringComparison.Ordinal);

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
