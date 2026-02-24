#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.CodeGeneration;
using Orleans.GrainReferences;
using Orleans.Hosting;
using Orleans.Metadata;
using Orleans.Reminders.Cron.Internal;
using Orleans.Runtime.ConsistentRing;
using Orleans.Runtime.Internal;
using Orleans.Runtime.Scheduler;
using Orleans.Statistics;

namespace Orleans.Runtime.ReminderService;

internal sealed class AdaptiveReminderService : GrainService, IReminderService, ILifecycleParticipant<ISiloLifecycle>
{
    private const int InitialReadRetryCountBeforeFastFailForUpdates = 2;
    private static readonly TimeSpan InitialReadMaxWaitTimeForUpdates = TimeSpan.FromSeconds(20);

    private readonly ILogger _logger;
    private readonly ReminderOptions _options;
    private readonly IReminderTable _reminderTable;
    private readonly IAsyncTimerFactory _asyncTimerFactory;
    private readonly IAsyncTimer _pollTimer;
    private readonly IAsyncTimer _repairTimer;
    private readonly TimeProvider _timeProvider;
    private readonly GrainReferenceActivator _referenceActivator;
    private readonly GrainInterfaceType _grainInterfaceType;
    private readonly IEnvironmentStatisticsProvider _environmentStatisticsProvider;
    private readonly IActivationWorkingSet _activationWorkingSet;
    private readonly TaskCompletionSource<bool> _startedTask = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Channel<ReminderEntry> _deliveryQueue;
    private readonly ConcurrentDictionary<ReminderIdentity, EnqueuedReminderState> _enqueuedReminders = new();
    private readonly ConcurrentDictionary<CronScheduleCacheKey, ReminderCronSchedule> _cronCache = new();
    private readonly List<Task> _workerTasks = new();

    private Task? _pollingTask;
    private Task? _repairTask;
    private uint _initialReadCallCount;

    public AdaptiveReminderService(
        GrainReferenceActivator referenceActivator,
        GrainInterfaceTypeResolver interfaceTypeResolver,
        IReminderTable reminderTable,
        IAsyncTimerFactory asyncTimerFactory,
        IOptions<ReminderOptions> reminderOptions,
        TimeProvider timeProvider,
        IEnvironmentStatisticsProvider environmentStatisticsProvider,
        IActivationWorkingSet activationWorkingSet,
        IConsistentRingProvider ringProvider,
        SystemTargetShared shared)
        : base(
            SystemTargetGrainId.CreateGrainServiceGrainId(GrainInterfaceUtils.GetGrainClassTypeCode(typeof(IReminderService)), string.Empty, shared.SiloAddress),
            ringProvider,
            shared)
    {
        _referenceActivator = referenceActivator;
        _grainInterfaceType = interfaceTypeResolver.GetGrainInterfaceType(typeof(IRemindable));
        _reminderTable = reminderTable;
        _asyncTimerFactory = asyncTimerFactory;
        _options = reminderOptions.Value;
        _timeProvider = timeProvider;
        _environmentStatisticsProvider = environmentStatisticsProvider;
        _activationWorkingSet = activationWorkingSet;
        _logger = shared.LoggerFactory.CreateLogger<AdaptiveReminderService>();

        _pollTimer = _asyncTimerFactory.Create(_options.PollInterval, nameof(AdaptiveReminderService) + ".Poll");
        _repairTimer = _asyncTimerFactory.Create(TimeSpan.FromMinutes(1), nameof(AdaptiveReminderService) + ".Repair");
        _deliveryQueue = Channel.CreateUnbounded<ReminderEntry>(new UnboundedChannelOptions { SingleWriter = false, SingleReader = false });

        shared.ActivationDirectory.RecordNewTarget(this);
    }

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
    {
        lifecycle.Subscribe(
            nameof(AdaptiveReminderService),
            ServiceLifecycleStage.BecomeActive,
            async ct =>
            {
                try
                {
                    await this.QueueTask(() => Initialize(ct));
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Error initializing adaptive reminder service.");
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
                    _logger.LogError(exception, "Error stopping adaptive reminder service.");
                    throw;
                }
            });

        lifecycle.Subscribe(
            nameof(AdaptiveReminderService),
            ServiceLifecycleStage.Active,
            async ct =>
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(_options.InitializationTimeout);

                try
                {
                    await this.QueueTask(Start).WaitAsync(cts.Token);
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Error starting adaptive reminder service.");
                    throw;
                }
            },
            _ => Task.CompletedTask);
    }

    private async Task Initialize(CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_options.InitializationTimeout);
        await _reminderTable.StartAsync(cts.Token);
    }

    public override async Task Stop()
    {
        await base.Stop();

        _pollTimer.Dispose();
        _repairTimer.Dispose();
        _deliveryQueue.Writer.TryComplete();

        var tasks = new List<Task>(_workerTasks.Count + 2);
        if (_pollingTask is not null)
        {
            tasks.Add(_pollingTask);
        }

        if (_repairTask is not null)
        {
            tasks.Add(_repairTask);
        }

        foreach (var workerTask in _workerTasks.Where(static task => task is not null))
        {
            tasks.Add(workerTask);
        }
        await Task.WhenAll(tasks);

        await _reminderTable.StopAsync();
    }

    protected override async Task StartInBackground()
    {
        await DoInitialPollAndQueue();

        if (StoppedCancellationTokenSource.IsCancellationRequested)
        {
            return;
        }

        Status = GrainServiceStatus.Started;
        _startedTask.TrySetResult(true);

        _pollingTask = RunPollLoopAsync();
        _repairTask = RunRepairLoopAsync();

        var workerCount = Math.Max(1, Environment.ProcessorCount * 4);
        for (var i = 0; i < workerCount; i++)
        {
            _workerTasks.Add(RunWorkerLoopAsync(i));
        }
    }

    public override Task OnRangeChange(IRingRange oldRange, IRingRange newRange, bool increased)
    {
        _ = base.OnRangeChange(oldRange, newRange, increased);

        RemoveOutOfRangeQueuedReminders();

        if (Status == GrainServiceStatus.Started)
        {
            return PollAndQueueDueReminders();
        }

        return Task.CompletedTask;
    }

    public Task<IGrainReminder> RegisterOrUpdateReminder(GrainId grainId, string reminderName, TimeSpan dueTime, TimeSpan period)
        => RegisterOrUpdateReminder(grainId, reminderName, dueTime, period, ReminderPriority.Normal, MissedReminderAction.Skip);

    public Task<IGrainReminder> RegisterOrUpdateReminder(GrainId grainId, string reminderName, DateTime dueAtUtc, TimeSpan period)
        => RegisterOrUpdateReminder(grainId, reminderName, dueAtUtc, period, ReminderPriority.Normal, MissedReminderAction.Skip);

    public async Task<IGrainReminder> RegisterOrUpdateReminder(
        GrainId grainId,
        string reminderName,
        TimeSpan dueTime,
        TimeSpan period,
        ReminderPriority priority,
        MissedReminderAction action)
    {
        var dueUtc = UtcNow.Add(dueTime);
        return await RegisterOrUpdateReminder(grainId, reminderName, dueUtc, period, priority, action);
    }

    public async Task<IGrainReminder> RegisterOrUpdateReminder(
        GrainId grainId,
        string reminderName,
        DateTime dueAtUtc,
        TimeSpan period,
        ReminderPriority priority,
        MissedReminderAction action)
    {
        if (dueAtUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Due timestamp must use DateTimeKind.Utc.", nameof(dueAtUtc));
        }

        var entry = new ReminderEntry
        {
            GrainId = grainId,
            ReminderName = reminderName,
            StartAt = dueAtUtc,
            Period = period,
            Priority = priority,
            Action = action,
            NextDueUtc = dueAtUtc,
            LastFireUtc = null,
            CronExpression = null,
        };

        await DoResponsibilitySanityCheck(grainId, "RegisterOrUpdateReminder");
        var etag = await _reminderTable.UpsertRow(entry);
        if (etag is null)
        {
            throw new ReminderException($"Could not register reminder {entry} due to storage contention. Please retry.");
        }

        entry.ETag = etag;
        TryQueueReminder(entry, UtcNow.Add(_options.LookAheadWindow));
        return new ReminderData(grainId, reminderName, etag, null, priority, action);
    }

    public Task<IGrainReminder> RegisterOrUpdateReminder(GrainId grainId, string reminderName, string cronExpression)
        => RegisterOrUpdateReminder(
            grainId,
            reminderName,
            cronExpression,
            priority: ReminderPriority.Normal,
            action: MissedReminderAction.Skip,
            cronTimeZoneId: null);

    public Task<IGrainReminder> RegisterOrUpdateReminder(GrainId grainId, string reminderName, string cronExpression, string? cronTimeZoneId)
        => RegisterOrUpdateReminder(
            grainId,
            reminderName,
            cronExpression,
            priority: ReminderPriority.Normal,
            action: MissedReminderAction.Skip,
            cronTimeZoneId: cronTimeZoneId);

    public async Task<IGrainReminder> RegisterOrUpdateReminder(
        GrainId grainId,
        string reminderName,
        string cronExpression,
        ReminderPriority priority,
        MissedReminderAction action)
        => await RegisterOrUpdateReminder(grainId, reminderName, cronExpression, priority, action, cronTimeZoneId: null);

    public async Task<IGrainReminder> RegisterOrUpdateReminder(
        GrainId grainId,
        string reminderName,
        string cronExpression,
        ReminderPriority priority,
        MissedReminderAction action,
        string? cronTimeZoneId)
    {
        var cronSchedule = GetCronSchedule(cronExpression, cronTimeZoneId);
        var now = UtcNow;
        var nextDue = cronSchedule.GetNextOccurrence(now);
        if (nextDue is null)
        {
            throw new ReminderException($"The cron expression '{cronExpression}' for reminder '{reminderName}' has no future occurrences.");
        }

        var entry = new ReminderEntry
        {
            GrainId = grainId,
            ReminderName = reminderName,
            StartAt = nextDue.Value,
            Period = TimeSpan.Zero,
            Priority = priority,
            Action = action,
            NextDueUtc = nextDue,
            LastFireUtc = null,
            CronExpression = cronSchedule.Expression.ToExpressionString(),
            CronTimeZoneId = cronSchedule.TimeZoneId,
        };

        await DoResponsibilitySanityCheck(grainId, "RegisterOrUpdateReminderCron");
        var etag = await _reminderTable.UpsertRow(entry);
        if (etag is null)
        {
            throw new ReminderException($"Could not register reminder {entry} due to storage contention. Please retry.");
        }

        entry.ETag = etag;
        TryQueueReminder(entry, UtcNow.Add(_options.LookAheadWindow));
        return new ReminderData(grainId, reminderName, etag, entry.CronExpression, priority, action, entry.CronTimeZoneId);
    }

    public async Task UnregisterReminder(IGrainReminder reminder)
    {
        var remData = (ReminderData)reminder;

        await DoResponsibilitySanityCheck(remData.GrainId, "UnregisterReminder");

        if (await _reminderTable.RemoveRow(remData.GrainId, remData.ReminderName, remData.ETag))
        {
            _enqueuedReminders.TryRemove(new(remData.GrainId, remData.ReminderName), out _);
            return;
        }

        var latest = await _reminderTable.ReadRow(remData.GrainId, remData.ReminderName);
        if (latest is null)
        {
            throw new ReminderException($"Could not unregister reminder {reminder} due to ETag mismatch.");
        }

        if (!await _reminderTable.RemoveRow(remData.GrainId, remData.ReminderName, latest.ETag))
        {
            throw new ReminderException($"Could not unregister reminder {reminder} due to ETag mismatch.");
        }

        _enqueuedReminders.TryRemove(new(remData.GrainId, remData.ReminderName), out _);
    }

    public async Task<IGrainReminder> GetReminder(GrainId grainId, string reminderName)
    {
        var entry = await _reminderTable.ReadRow(grainId, reminderName);
        return entry is null ? null! : entry.ToIGrainReminder();
    }

    public async Task<List<IGrainReminder>> GetReminders(GrainId grainId)
    {
        var tableData = await _reminderTable.ReadRows(grainId);
        return tableData.Reminders.Select(static entry => entry.ToIGrainReminder()).ToList();
    }

    internal static int CalculateAdaptiveBucketSize(uint baseBucketSize, int processorCount, double memoryLoadFraction, int activeGrainCount)
    {
        var cpuFactor = Math.Max(1, processorCount / 4);
        var memoryFactor = Math.Max(0.25, 1d - memoryLoadFraction);
        var grainFactor = activeGrainCount <= 0 ? 1d : Math.Min(1d, 50_000d / activeGrainCount);
        var computed = baseBucketSize * (double)cpuFactor * memoryFactor * grainFactor;
        return Math.Max(1, (int)Math.Round(computed, MidpointRounding.AwayFromZero));
    }

    private async Task DoInitialPollAndQueue()
    {
        while (!StoppedCancellationTokenSource.IsCancellationRequested)
        {
            try
            {
                _initialReadCallCount++;
                await PollAndQueueDueReminders();
                return;
            }
            catch (Exception ex)
            {
                if (_initialReadCallCount <= InitialReadRetryCountBeforeFastFailForUpdates)
                {
                    _logger.LogWarning(ex, "Adaptive reminder service initial poll failed. Attempt {Attempt}", _initialReadCallCount);
                    var retryDelay = GetInitialPollRetryDelay(_initialReadCallCount);
                    try
                    {
                        await Task.Delay(retryDelay, _timeProvider, StoppedCancellationTokenSource.Token);
                    }
                    catch (OperationCanceledException) when (StoppedCancellationTokenSource.IsCancellationRequested)
                    {
                        return;
                    }

                    continue;
                }

                _logger.LogError(ex, "Adaptive reminder service failed initial poll after {Attempt} attempts.", _initialReadCallCount);
                var failure = new OrleansException("Adaptive reminder service failed initial poll and cannot safely start.", ex);
                _startedTask.TrySetException(failure);
                throw failure;
            }
        }
    }

    private static TimeSpan GetInitialPollRetryDelay(uint attempt)
    {
        // Exponential backoff with bounded jitter to avoid burst retries against a struggling store.
        var exponent = (int)Math.Min(5, Math.Max(0, attempt - 1));
        var baseDelayMs = 250 * (1 << exponent);
        var jitterMs = Random.Shared.Next(25, 250);
        return TimeSpan.FromMilliseconds(baseDelayMs + jitterMs);
    }

    private async Task RunPollLoopAsync()
    {
        await Task.Yield();

        while (await _pollTimer.NextTick())
        {
            if (StoppedCancellationTokenSource.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await PollAndQueueDueReminders();
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Exception while polling reminders.");
            }
        }
    }

    private async Task RunRepairLoopAsync()
    {
        await Task.Yield();

        while (await _repairTimer.NextTick())
        {
            if (StoppedCancellationTokenSource.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await RepairOverdueRows();
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Exception while repairing overdue reminders.");
            }
        }
    }

    private async Task RunWorkerLoopAsync(int workerIndex)
    {
        await Task.Yield();

        var token = StoppedCancellationTokenSource.Token;
        try
        {
            while (await _deliveryQueue.Reader.WaitToReadAsync(token))
            {
                while (_deliveryQueue.Reader.TryRead(out var entry))
                {
                    await ProcessReminderAsync(entry, workerIndex, token);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Expected during shutdown.
        }
    }

    private async Task ProcessReminderAsync(ReminderEntry entry, int workerIndex, CancellationToken cancellationToken)
    {
        var identity = new ReminderIdentity(entry.GrainId, entry.ReminderName);
        var queuedState = CreateQueuedState(entry);
        try
        {
            if (!RingRange.InRange(entry.GrainId))
            {
                _logger.LogTrace(
                    "Skipping reminder {Reminder} for grain {GrainId} because it is no longer in this silo's range.",
                    entry.ReminderName,
                    entry.GrainId);
                return;
            }

            if (!IsCurrentQueuedState(identity, queuedState))
            {
                _logger.LogTrace(
                    "Skipping stale queued reminder {Reminder} for grain {GrainId}.",
                    entry.ReminderName,
                    entry.GrainId);
                return;
            }

            var now = UtcNow;
            var due = queuedState.DueUtc;

            if (due > now)
            {
                var waitTime = due - now;
                if (waitTime > TimeSpan.Zero)
                {
                    await Task.Delay(waitTime, _timeProvider, cancellationToken);
                }
            }

            if (!RingRange.InRange(entry.GrainId) || !IsCurrentQueuedState(identity, queuedState))
            {
                return;
            }

            now = UtcNow;

            var overdueBy = now > due ? now - due : TimeSpan.Zero;
            // Treat reminders as "missed" only after a full poll interval to avoid dropping ticks due to scheduler jitter.
            var isMissed = overdueBy > _options.PollInterval;
            var shouldFire = true;
            if (isMissed)
            {
                switch (entry.Action)
                {
                    case MissedReminderAction.Skip:
                        shouldFire = false;
                        break;
                    case MissedReminderAction.Notify:
                        shouldFire = false;
                        _logger.LogWarning(
                            "Reminder {Reminder} for grain {GrainId} missed due window at {Due}. Current time {Now}.",
                            entry.ReminderName,
                            entry.GrainId,
                            due,
                            now);
                        break;
                }
            }

            if (shouldFire)
            {
                var remindable = GetGrain(entry.GrainId);
                var status = new TickStatus(
                    entry.StartAt,
                    string.IsNullOrWhiteSpace(entry.CronExpression) ? entry.Period : TimeSpan.Zero,
                    now,
                    string.IsNullOrWhiteSpace(entry.CronExpression) ? ReminderScheduleKind.Interval : ReminderScheduleKind.Cron);

                await remindable.ReceiveReminder(entry.ReminderName, status);
                entry.LastFireUtc = now;
            }

            var nextDue = CalculateNextDue(entry, now);
            if (nextDue is null)
            {
                _logger.LogWarning(
                    "Reminder {Reminder} for grain {GrainId} has no future occurrences and will no longer be scheduled.",
                    entry.ReminderName,
                    entry.GrainId);
                return;
            }

            entry.NextDueUtc = nextDue;
            var etag = await _reminderTable.UpsertRow(entry);
            if (etag is not null)
            {
                entry.ETag = etag;
            }

            _logger.LogTrace(
                "Worker {Worker} processed reminder {Reminder} for grain {GrainId}. Next due at {NextDueUtc}.",
                workerIndex,
                entry.ReminderName,
                entry.GrainId,
                entry.NextDueUtc);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected during shutdown.
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error processing reminder {Reminder} for grain {GrainId}.", entry.ReminderName, entry.GrainId);
        }
        finally
        {
            TryRemoveQueuedState(identity, queuedState);
        }
    }

    private async Task PollAndQueueDueReminders()
    {
        if (StoppedCancellationTokenSource.IsCancellationRequested)
        {
            return;
        }

        RemoveOutOfRangeQueuedReminders();

        var rangeSerial = RangeSerialNumber;
        var now = UtcNow;
        var horizon = now.Add(_options.LookAheadWindow);
        var environment = _environmentStatisticsProvider.GetEnvironmentStatistics();
        var memoryLoad = Math.Clamp(environment.NormalizedMemoryUsage, 0f, 1f);
        var bucketSize = CalculateAdaptiveBucketSize(
            _options.BaseBucketSize,
            Environment.ProcessorCount,
            memoryLoad,
            _activationWorkingSet.Count);

        // Keep a bounded candidate set to avoid loading all due rows into memory under heavy reminder volumes.
        var selectionLimit = bucketSize <= int.MaxValue / 4 ? bucketSize * 4 : int.MaxValue;
        var selectedCandidates = new PriorityQueue<ReminderEntry, ReminderEntry>(new ReverseReminderEntryComparer(_options.EnablePriority));
        var candidateCount = 0;

        foreach (var range in RangeFactory.GetSubRanges(RingRange))
        {
            var table = await _reminderTable.ReadRows(range.Begin, range.End);
            if (rangeSerial < RangeSerialNumber)
            {
                _logger.LogDebug("Ring range changed during adaptive reminder poll. Ignoring current batch.");
                return;
            }

            if (table is null)
            {
                continue;
            }

            foreach (var entry in table.Reminders)
            {
                if (TryPrepareEntryForScheduling(entry, now, horizon))
                {
                    candidateCount++;
                    AddCandidate(selectedCandidates, entry, selectionLimit, _options.EnablePriority);
                }
            }
        }

        if (selectedCandidates.Count == 0)
        {
            return;
        }

        var candidates = ToOrderedList(selectedCandidates, _options.EnablePriority);

        var scheduled = 0;
        foreach (var entry in candidates)
        {
            if (scheduled >= bucketSize)
            {
                break;
            }

            if (TryQueueReminder(entry, horizon))
            {
                scheduled++;
            }
        }

        _logger.LogTrace(
            "Adaptive reminder poll complete. Candidates={Candidates}, Selected={Selected}, Scheduled={Scheduled}, BucketSize={BucketSize}, ActiveQueued={Queued}.",
            candidateCount,
            candidates.Count,
            scheduled,
            bucketSize,
            _enqueuedReminders.Count);
    }

    private async Task RepairOverdueRows()
    {
        var now = UtcNow;
        var overdueThreshold = now - _options.LookAheadWindow;

        foreach (var range in RangeFactory.GetSubRanges(RingRange))
        {
            var table = await _reminderTable.ReadRows(range.Begin, range.End);
            if (table is null)
            {
                continue;
            }

            foreach (var entry in table.Reminders)
            {
                if (entry.NextDueUtc is not { } nextDue || nextDue >= overdueThreshold)
                {
                    continue;
                }

                var repairedNextDue = CalculateNextDue(entry, now);
                if (repairedNextDue is null)
                {
                    continue;
                }

                entry.NextDueUtc = repairedNextDue;
                var etag = await _reminderTable.UpsertRow(entry);
                if (etag is not null)
                {
                    entry.ETag = etag;
                }

                _logger.LogDebug(
                    "Repaired overdue reminder {Reminder} for grain {GrainId}: {OldDue} -> {NewDue}.",
                    entry.ReminderName,
                    entry.GrainId,
                    nextDue,
                    repairedNextDue);
            }
        }
    }

    private bool TryPrepareEntryForScheduling(ReminderEntry entry, DateTime now, DateTime horizon)
    {
        if (entry is null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(entry.CronExpression) && entry.Period <= TimeSpan.Zero)
        {
            return false;
        }

        if (!Enum.IsDefined(entry.Priority))
        {
            entry.Priority = ReminderPriority.Normal;
        }

        if (!Enum.IsDefined(entry.Action))
        {
            entry.Action = MissedReminderAction.Skip;
        }

        var due = entry.NextDueUtc ?? entry.StartAt;
        if (due.Kind == DateTimeKind.Unspecified)
        {
            due = DateTime.SpecifyKind(due, DateTimeKind.Utc);
        }

        if (entry.NextDueUtc is null)
        {
            entry.NextDueUtc = due;
        }

        return due <= horizon;
    }

    private bool TryQueueReminder(ReminderEntry entry, DateTime horizon)
    {
        var due = entry.NextDueUtc ?? entry.StartAt;
        if (due > horizon)
        {
            return false;
        }

        var identity = new ReminderIdentity(entry.GrainId, entry.ReminderName);
        var queuedState = new EnqueuedReminderState(entry.ETag, due);
        while (true)
        {
            if (_enqueuedReminders.TryGetValue(identity, out var existing))
            {
                if (existing.Equals(queuedState))
                {
                    return false;
                }

                if (_enqueuedReminders.TryUpdate(identity, queuedState, existing))
                {
                    break;
                }

                continue;
            }

            if (_enqueuedReminders.TryAdd(identity, queuedState))
            {
                break;
            }
        }

        if (!_deliveryQueue.Writer.TryWrite(CloneEntry(entry)))
        {
            TryRemoveQueuedState(identity, queuedState);
            return false;
        }

        return true;
    }

    private void RemoveOutOfRangeQueuedReminders()
    {
        foreach (var item in _enqueuedReminders.Where(item => !RingRange.InRange(item.Key.GrainId)))
        {
            _enqueuedReminders.TryRemove(item.Key, out _);
        }
    }

    private static EnqueuedReminderState CreateQueuedState(ReminderEntry entry)
        => new(entry.ETag, entry.NextDueUtc ?? entry.StartAt);

    private bool IsCurrentQueuedState(ReminderIdentity identity, EnqueuedReminderState state)
        => _enqueuedReminders.TryGetValue(identity, out var current) && current.Equals(state);

    private bool TryRemoveQueuedState(ReminderIdentity identity, EnqueuedReminderState state)
        => ((ICollection<KeyValuePair<ReminderIdentity, EnqueuedReminderState>>)_enqueuedReminders)
            .Remove(new KeyValuePair<ReminderIdentity, EnqueuedReminderState>(identity, state));

    private DateTime? CalculateNextDue(ReminderEntry entry, DateTime now)
    {
        if (!string.IsNullOrWhiteSpace(entry.CronExpression))
        {
            var cronSchedule = GetCronSchedule(entry.CronExpression, entry.CronTimeZoneId);
            return cronSchedule.GetNextOccurrence(now);
        }

        var period = entry.Period;
        if (period <= TimeSpan.Zero)
        {
            return null;
        }

        var next = entry.NextDueUtc ?? entry.StartAt;
        if (next <= now)
        {
            var ticksBehind = now.Ticks - next.Ticks;
            var periodsBehind = ticksBehind / period.Ticks + 1;
            next = next.AddTicks(periodsBehind * period.Ticks);
        }

        return next;
    }

    private ReminderCronSchedule GetCronSchedule(string expression, string? cronTimeZoneId)
    {
        var key = new CronScheduleCacheKey(expression.Trim(), string.IsNullOrWhiteSpace(cronTimeZoneId) ? null : cronTimeZoneId.Trim());
        return _cronCache.GetOrAdd(
            key,
            static value => ReminderCronSchedule.Parse(value.Expression, value.TimeZoneId));
    }

    private IRemindable GetGrain(GrainId grainId)
        => (IRemindable)_referenceActivator.CreateReference(grainId, _grainInterfaceType);

    private Task DoResponsibilitySanityCheck(GrainId grainId, string operation)
    {
        switch (Status)
        {
            case GrainServiceStatus.Booting:
                var task = _startedTask.Task;
                if (task.IsCompleted)
                {
                    task.GetAwaiter().GetResult();
                }
                else
                {
                    return WaitForInitCompletion();
                }

                break;
            case GrainServiceStatus.Started:
                break;
            case GrainServiceStatus.Stopped:
                throw new OperationCanceledException("Adaptive reminder service has been stopped.");
            default:
                throw new InvalidOperationException($"Unknown status {Status}");
        }

        CheckRange();
        return Task.CompletedTask;

        async Task WaitForInitCompletion()
        {
            try
            {
                await _startedTask.Task.WaitAsync(InitialReadMaxWaitTimeForUpdates);
            }
            catch (TimeoutException ex)
            {
                throw new OrleansException("Adaptive reminder service is still initializing. Please retry.", ex);
            }

            CheckRange();
        }

        void CheckRange()
        {
            if (!RingRange.InRange(grainId))
            {
                _logger.LogWarning(
                    "Operation '{Operation}' for grain {GrainId} is outside this silo's ring range {Range}.",
                    operation,
                    grainId,
                    RingRange);
            }
        }
    }

    private int CompareReminderEntries(ReminderEntry left, ReminderEntry right)
        => CompareReminderEntries(left, right, _options.EnablePriority);

    internal static List<ReminderEntry> SelectTopCandidatesForBucket(IEnumerable<ReminderEntry> candidates, int selectionLimit, bool enablePriority)
    {
        if (selectionLimit <= 0)
        {
            return [];
        }

        var selected = new PriorityQueue<ReminderEntry, ReminderEntry>(new ReverseReminderEntryComparer(enablePriority));
        foreach (var candidate in candidates)
        {
            AddCandidate(selected, candidate, selectionLimit, enablePriority);
        }

        return ToOrderedList(selected, enablePriority);
    }

    private static void AddCandidate(
        PriorityQueue<ReminderEntry, ReminderEntry> selected,
        ReminderEntry candidate,
        int selectionLimit,
        bool enablePriority)
    {
        if (selectionLimit <= 0)
        {
            return;
        }

        if (selected.Count < selectionLimit)
        {
            var clone = CloneEntry(candidate);
            selected.Enqueue(clone, clone);
            return;
        }

        var worst = selected.Peek();
        if (CompareReminderEntries(candidate, worst, enablePriority) < 0)
        {
            selected.Dequeue();
            var clone = CloneEntry(candidate);
            selected.Enqueue(clone, clone);
        }
    }

    private static List<ReminderEntry> ToOrderedList(
        PriorityQueue<ReminderEntry, ReminderEntry> selected,
        bool enablePriority)
    {
        var result = new List<ReminderEntry>(selected.Count);
        foreach (var item in selected.UnorderedItems)
        {
            result.Add(item.Element);
        }

        result.Sort((left, right) => CompareReminderEntries(left, right, enablePriority));
        return result;
    }

    private static int CompareReminderEntries(ReminderEntry left, ReminderEntry right, bool enablePriority)
    {
        var leftPriority = enablePriority ? left.Priority : ReminderPriority.Normal;
        var rightPriority = enablePriority ? right.Priority : ReminderPriority.Normal;

        var priorityCompare = rightPriority.CompareTo(leftPriority);
        if (priorityCompare != 0)
        {
            return priorityCompare;
        }

        var leftDue = left.NextDueUtc ?? left.StartAt;
        var rightDue = right.NextDueUtc ?? right.StartAt;
        var dueCompare = leftDue.CompareTo(rightDue);
        if (dueCompare != 0)
        {
            return dueCompare;
        }

        var grainCompare = left.GrainId.CompareTo(right.GrainId);
        if (grainCompare != 0)
        {
            return grainCompare;
        }

        return string.CompareOrdinal(left.ReminderName, right.ReminderName);
    }

    private static ReminderEntry CloneEntry(ReminderEntry entry)
    {
        return new ReminderEntry
        {
            GrainId = entry.GrainId,
            ReminderName = entry.ReminderName,
            StartAt = entry.StartAt,
            Period = entry.Period,
            ETag = entry.ETag,
            CronExpression = entry.CronExpression,
            CronTimeZoneId = entry.CronTimeZoneId,
            NextDueUtc = entry.NextDueUtc,
            LastFireUtc = entry.LastFireUtc,
            Priority = entry.Priority,
            Action = entry.Action,
        };
    }

    private readonly record struct EnqueuedReminderState(string? ETag, DateTime DueUtc);

    private readonly record struct ReminderIdentity(GrainId GrainId, string ReminderName);

    private readonly record struct CronScheduleCacheKey(string Expression, string? TimeZoneId);

    private sealed class ReverseReminderEntryComparer(bool enablePriority) : IComparer<ReminderEntry>
    {
        public int Compare(ReminderEntry? x, ReminderEntry? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            // PriorityQueue dequeues the "smallest" priority first. We reverse sort order so the "worst" entry stays at the root.
            var compare = CompareReminderEntries(x, y, enablePriority);
            if (compare < 0)
            {
                return 1;
            }

            if (compare > 0)
            {
                return -1;
            }

            return 0;
        }
    }
}
