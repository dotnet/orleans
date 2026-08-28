using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.DurableJobs;
using Orleans.DurableMessaging.Configuration;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Session;
using Orleans.Serialization.TypeSystem;
using Orleans.Timers;

namespace Orleans.DurableMessaging;

/// <summary>
/// Implementation of durable inbox extension for grain message delivery.
/// Handles message persistence, deduplication, and processing.
/// </summary>
internal sealed partial class DurableInboxExtension :
    IDurableInboxExtension,
    IDurableJobFeatureHandler,
    IJournaledStateObserver,
    ILifecycleObserver,
    IDisposable
{
    internal const string JobName = "orleans.messaging.inbox-drain";

    public bool CanHandle(string jobName) => string.Equals(jobName, JobName, StringComparison.Ordinal);

    private readonly IGrainContext _grainContext;
    private readonly IGrainFactory _grainFactory;
    private readonly ITimerRegistry _timerRegistry;
    private readonly IJournaledStateManager _stateManager;
    private readonly SerializerSessionPool _sessionPool;
    private readonly ILogger<DurableInboxExtension> _logger;
    private readonly DurableMessagingInstruments _instruments;
    private readonly DurableInbox _durableInbox;
    private readonly IDictionary<(GrainId SenderId, Guid MessageId), DurableEnvelope> _inboxDict;
    private readonly IDictionary<(GrainId SenderId, Guid MessageId), DateTimeOffset> _processed;
    private readonly IDictionary<(GrainId SenderId, Guid MessageId), InboxMessageState> _messageStates;
    private readonly IDictionary<(GrainId SenderId, Guid MessageId), InboxDeadLetter> _deadLetters;
    private readonly IDurableValue<string> _jobId;
    private readonly IDurableValue<string> _completedJobId;
    private readonly IDurableValue<long> _jobSequence;
    private readonly IDurableOutbox _outbox;
    private readonly ILocalDurableJobManager _jobManager;
    private readonly TimeProvider _timeProvider;
    private readonly TimeProvider _jobTimeProvider;
    private readonly HashSet<(GrainId SenderId, Guid MessageId)> _provisionalAcceptances = [];
    private readonly HashSet<string> _localDrainJobIds = new(StringComparer.Ordinal);
    private readonly DurableMessagingPumpResults _pumpResults;
    private readonly int _maxCapacity;
    private readonly TimeSpan _deduplicationWindow;
    private readonly int _maxProcessingAttempts;
    private readonly int _batchSize;
    private readonly TimeSpan _retryDelay;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdownCts = new();
    private int _metricsActive;
    private int _reportedDepth;
    private int _handlerExecutionDepth;
    private DateTimeOffset? _pendingJobDueTime;
    private bool _provisionalScheduleConfirmed;
    private string _ownershipEpoch = Guid.NewGuid().ToString("N");
    private long _stateGeneration;
    private bool _recoveryCompleted;

    /// <summary>
    /// Creates a new inbox extension instance.
    /// </summary>
    /// <param name="grainContext">The grain context for this extension.</param>
    /// <param name="stateManager">State manager for atomic persistence.</param>
    /// <param name="sessionPool">Serializer session pool for envelope creation.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="instruments">Journaling metrics.</param>
    /// <param name="durableInbox">The grain's durable inbox (shared with grain DI).</param>
    /// <param name="inboxDict">Durable dictionary for inbox messages.</param>
    /// <param name="processed">Durable dictionary for processed message tracking.</param>
    /// <param name="outbox">Durable outbox for sending response messages.</param>
    /// <param name="options">Durable messaging options.</param>
    public DurableInboxExtension(
        IGrainContext grainContext,
        IGrainFactory grainFactory,
        ITimerRegistry timerRegistry,
        IJournaledStateManager stateManager,
        SerializerSessionPool sessionPool,
        ILogger<DurableInboxExtension> logger,
        DurableMessagingInstruments instruments,
        DurableInbox durableInbox,
        IDictionary<(GrainId SenderId, Guid MessageId), DurableEnvelope> inboxDict,
        IDictionary<(GrainId SenderId, Guid MessageId), DateTimeOffset> processed,
        IDictionary<(GrainId SenderId, Guid MessageId), InboxMessageState> messageStates,
        IDictionary<(GrainId SenderId, Guid MessageId), InboxDeadLetter> deadLetters,
        IDurableValue<string> jobId,
        IDurableValue<string> completedJobId,
        IDurableValue<long> jobSequence,
        IDurableOutbox outbox,
        ILocalDurableJobManager jobManager,
        IDurableJobHandlerRegistry jobHandlers,
        DurableMessagingPumpResults pumpResults,
        TimeProvider timeProvider,
        TimeProvider jobTimeProvider,
        DurableInboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(grainContext);
        ArgumentNullException.ThrowIfNull(grainFactory);
        ArgumentNullException.ThrowIfNull(timerRegistry);
        ArgumentNullException.ThrowIfNull(stateManager);
        ArgumentNullException.ThrowIfNull(sessionPool);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(instruments);
        ArgumentNullException.ThrowIfNull(durableInbox);
        ArgumentNullException.ThrowIfNull(inboxDict);
        ArgumentNullException.ThrowIfNull(processed);
        ArgumentNullException.ThrowIfNull(messageStates);
        ArgumentNullException.ThrowIfNull(deadLetters);
        ArgumentNullException.ThrowIfNull(jobId);
        ArgumentNullException.ThrowIfNull(completedJobId);
        ArgumentNullException.ThrowIfNull(jobSequence);
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(jobManager);
        ArgumentNullException.ThrowIfNull(jobHandlers);
        ArgumentNullException.ThrowIfNull(pumpResults);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(jobTimeProvider);
        ArgumentNullException.ThrowIfNull(options);
        _grainContext = grainContext;
        _grainFactory = grainFactory;
        _timerRegistry = timerRegistry;
        _stateManager = stateManager;
        _sessionPool = sessionPool;
        _logger = logger;
        _instruments = instruments;
        _durableInbox = durableInbox;
        _inboxDict = inboxDict;
        _processed = processed;
        _messageStates = messageStates;
        _deadLetters = deadLetters;
        _jobId = jobId;
        _completedJobId = completedJobId;
        _jobSequence = jobSequence;
        _outbox = outbox;
        _jobManager = jobManager;
        _pumpResults = pumpResults;
        _timeProvider = timeProvider;
        _jobTimeProvider = jobTimeProvider;
        _maxCapacity = options.MaxCapacity;
        _deduplicationWindow = options.DeduplicationWindow;
        _maxProcessingAttempts = options.MaxProcessingAttempts;
        _batchSize = options.InboxBatchSize;
        _retryDelay = options.BackpressureRetryDelay;
        DurableMessagingStateManagerCapabilities.RegisterObserver(stateManager, this);
        jobHandlers.Register(this);
        grainContext.ObservableLifecycle.Subscribe(
            RuntimeTypeNameFormatter.Format(GetType()),
            GrainLifecycleStage.Activate,
            this);
    }

    /// <summary>
    /// Gets the number of messages currently in the inbox.
    /// </summary>
    public int Count => _inboxDict.Count;

    /// <summary>
    /// Gets the inbox capacity limit.
    /// </summary>
    public int Capacity => _maxCapacity;

    /// <summary>
    /// Registers a handler for a specific route key.
    /// Delegates to the shared durable inbox that is injected into grains.
    /// </summary>
    /// <param name="routeKey">The route key to handle.</param>
    /// <param name="handler">The handler implementation.</param>
    public void RegisterHandler(string routeKey, IInboxHandler handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeKey);
        ArgumentNullException.ThrowIfNull(handler);

        _durableInbox.RegisterHandler(routeKey, handler);
        LogHandlerRegistered(_logger, routeKey, _grainContext.GrainId);
    }

    /// <summary>
    /// Checks if a handler is registered for the specified route key.
    /// </summary>
    /// <param name="routeKey">The route key to check.</param>
    /// <returns>True if a handler is registered; otherwise, false.</returns>
    public bool HasHandler(string routeKey) => _durableInbox.HasHandler(routeKey);

    /// <summary>
    /// Tries to get a handler for the specified route key.
    /// </summary>
    /// <param name="routeKey">The route key to get the handler for.</param>
    /// <param name="handler">The handler if found.</param>
    /// <returns>True if a handler is registered; otherwise, false.</returns>
    public bool TryGetHandler(string routeKey, [MaybeNullWhen(false)] out IInboxHandler handler) => _durableInbox.TryGetHandler(routeKey, out handler);

    /// <summary>
    /// Delivers a message to this grain's durable inbox.
    /// </summary>
    /// <param name="envelope">The message envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result indicating delivery/processing status.</returns>
    public async ValueTask<DeliveryResult> DeliverAsync(
        DurableEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureMetricsActive();
        var key = (envelope.SenderId, envelope.MessageId);
        var result = DeliveryResult.Accepted();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            var replaceExpiredDedupeRecord = false;
            if (_processed.TryGetValue(key, out var processedAt))
            {
                replaceExpiredDedupeRecord = _timeProvider.GetUtcNow() - processedAt >= _deduplicationWindow;
                if (!replaceExpiredDedupeRecord)
                {
                    LogDuplicateMessageDetected(
                        _logger,
                        envelope.MessageId,
                        envelope.SenderId,
                        envelope.ReceiverId,
                        envelope.RouteKey,
                        envelope.CorrelationKey?.ToString());
                    _instruments.OnInboxMessageReceived(_grainContext.GrainId.Type.ToString(), envelope.RouteKey, "duplicate");
                    return DeliveryResult.Duplicate();
                }
            }

            if (_inboxDict.ContainsKey(key))
            {
                LogDuplicateMessageInInbox(
                    _logger,
                    envelope.MessageId,
                    envelope.SenderId,
                    envelope.ReceiverId,
                    envelope.RouteKey,
                    envelope.CorrelationKey?.ToString());
                _instruments.OnInboxMessageReceived(_grainContext.GrainId.Type.ToString(), envelope.RouteKey, "duplicate");
                await EnsureJobScheduledUnderGateAsync(CancellationToken.None).ConfigureAwait(true);
                ScheduleLocalDrain();
                result = DeliveryResult.Duplicate();
            }
            else
            {
                var wasDurablyEmpty = GetDurableInboxCount() == 0;
                if (_inboxDict.Count >= _maxCapacity)
                {
                    LogBackpressureRejection(
                        _logger,
                        _inboxDict.Count,
                        _maxCapacity,
                        _grainContext.GrainId,
                        envelope.MessageId,
                        envelope.SenderId,
                        envelope.RouteKey,
                        envelope.CorrelationKey?.ToString());
                    _instruments.OnInboxMessageReceived(_grainContext.GrainId.Type.ToString(), envelope.RouteKey, "backpressured");
                    return DeliveryResult.Backpressured();
                }

                var handlerContext = new InboxHandlerContext(envelope, _grainContext.GrainId, _outbox, _sessionPool);
                if (!_durableInbox.TryFindHandler(handlerContext, out _))
                {
                    LogRouteNotFound(
                        _logger,
                        envelope.RouteKey,
                        _grainContext.GrainId,
                        envelope.MessageId,
                        envelope.SenderId,
                        envelope.CorrelationKey?.ToString());
                    _instruments.OnInboxMessageReceived(_grainContext.GrainId.Type.ToString(), envelope.RouteKey, "route_not_found");
                    return DeliveryResult.RouteNotFound(envelope.RouteKey);
                }

                if (replaceExpiredDedupeRecord)
                {
                    _processed.Remove(key);
                }

                _inboxDict[key] = envelope;
                _messageStates[key] = new InboxMessageState();
                _provisionalAcceptances.Add(key);
                _provisionalScheduleConfirmed = false;
                var stateGeneration = Volatile.Read(ref _stateGeneration);
                UpdateInboxDepth(1);
                var committed = false;
                try
                {
                    await EnsureJobScheduledUnderGateAsync(
                        CancellationToken.None,
                        persistState: false,
                        replaceExisting: wasDurablyEmpty,
                        includeProvisional: true).ConfigureAwait(true);
                    _provisionalScheduleConfirmed = true;
                    ValidateAcceptanceState(key, stateGeneration);
                    await _stateManager.WriteStateAsync(CancellationToken.None).ConfigureAwait(true);
                    ValidateAcceptanceState(key, stateGeneration);
                    committed = true;
                }
                catch
                {
                    await _stateManager.RevertPendingChangesAsync(CancellationToken.None).ConfigureAwait(true);
                    throw;
                }
                finally
                {
                    _provisionalAcceptances.Remove(key);
                    if (_provisionalAcceptances.Count == 0)
                    {
                        _provisionalScheduleConfirmed = false;
                    }
                }

                if (committed && string.IsNullOrEmpty(_jobId.Value))
                {
                    await EnsureJobScheduledUnderGateAsync(CancellationToken.None).ConfigureAwait(true);
                }

                ScheduleLocalDrain();

                LogMessageAccepted(
                    _logger,
                    envelope.MessageId,
                    envelope.SenderId,
                    envelope.ReceiverId,
                    envelope.RouteKey,
                    envelope.CorrelationKey?.ToString());
                _instruments.OnInboxMessageReceived(_grainContext.GrainId.Type.ToString(), envelope.RouteKey, "accepted");
            }
        }
        finally
        {
            _gate.Release();
        }

        return result;
    }

    private async ValueTask EnsureJobScheduledUnderGateAsync(
        CancellationToken cancellationToken,
        bool persistState = true,
        bool replaceExisting = false,
        bool includeProvisional = false)
    {
        var messageCount = includeProvisional ? _inboxDict.Count : GetDurableInboxCount();
        if (messageCount == 0 || (!replaceExisting && !string.IsNullOrEmpty(_jobId.Value)))
        {
            return;
        }

        var previousJobId = _jobId.Value;
        var jobId = replaceExisting || string.IsNullOrEmpty(previousJobId)
            ? DurableMessagingJobOwnership.NextId(_ownershipEpoch, _jobSequence)
            : previousJobId;
        var dueTime = _pendingJobDueTime ??= _jobTimeProvider.GetUtcNow();
        _jobId.Value = jobId;
        try
        {
            await _jobManager.ScheduleJobAsync(
                new ScheduleJobRequest
                {
                    JobId = DurableMessagingJobOwnership.CreateJobId(JobName, _grainContext.GrainId, jobId),
                    Target = _grainContext.GrainId,
                    JobName = JobName,
                    DueTime = dueTime,
                    Metadata = DurableMessagingJobOwnership.CreateMetadata(jobId)
                },
                cancellationToken).ConfigureAwait(true);
        }
        catch
        {
            _jobId.Value = previousJobId;
            throw;
        }

        if (persistState)
        {
            await _stateManager.WriteStateAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    public void OnWriteStarted()
    {
    }

    public ValueTask OnWritePreparingAsync(CancellationToken cancellationToken)
    {
        return ValidatePersistenceBoundary(cancellationToken);
    }

    public void OnWriteRequested() => ValidatePersistenceRequest();

    public void OnDeleteRequested() => ValidatePersistenceRequest();

    public ValueTask OnDeletePreparingAsync(CancellationToken cancellationToken)
    {
        return ValidatePersistenceBoundary(cancellationToken);
    }

    public void OnDeleteCompleted()
    {
        Interlocked.Increment(ref _stateGeneration);
        _ownershipEpoch = Guid.NewGuid().ToString("N");
        _provisionalAcceptances.Clear();
        _provisionalScheduleConfirmed = false;
        _pendingJobDueTime = null;
        ReconcileInboxDepth();
    }

    public ValueTask OnWriteFinalizingAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_provisionalAcceptances.Count > 0 && !_provisionalScheduleConfirmed)
        {
            return ValueTask.FromException(
                new InvalidOperationException(
                    "Journaled state cannot be captured while durable inbox acceptance is waiting for job scheduling."));
        }

        return default;
    }

    private ValueTask ValidatePersistenceBoundary(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Volatile.Read(ref _handlerExecutionDepth) != 0
            ? ValueTask.FromException(
                new InvalidOperationException(
                    "Journaled state cannot be committed or deleted from inside a durable inbox handler. "
                    + "Handler effects, outgoing messages, and inbox completion are committed atomically after the handler returns."))
            : default;
    }

    private void ValidatePersistenceRequest()
    {
        if (Volatile.Read(ref _handlerExecutionDepth) != 0)
        {
            throw new InvalidOperationException(
                "Journaled state cannot be committed or deleted from inside a durable inbox handler. "
                + "Handler effects, outgoing messages, and inbox completion are committed atomically after the handler returns.");
        }
    }

    public void OnWriteCompleted()
    {
        _pendingJobDueTime = null;
    }

    public void OnRecoveryCompleted()
    {
        Interlocked.Increment(ref _stateGeneration);
        _ownershipEpoch = Guid.NewGuid().ToString("N");
        _recoveryCompleted = true;
        _provisionalAcceptances.Clear();
        _provisionalScheduleConfirmed = false;
        ReconcileInboxDepth();
    }

    public void OnRecoveryStarted() => _recoveryCompleted = false;

    public async ValueTask<DurableJobRunResult> ExecuteJobAsync(IJobRunContext context, CancellationToken cancellationToken)
    {
        var hasStableOwnership = DurableMessagingJobOwnership.TryGetOwnershipId(
            context.Job,
            out var ownershipId);
        if (!string.Equals(_jobId.Value, ownershipId, StringComparison.Ordinal))
        {
            if (!hasStableOwnership)
            {
                return DurableJobRunResult.Completed;
            }

            var disposition = DurableMessagingJobOwnership.ResolveMismatch(
                _recoveryCompleted,
                !string.IsNullOrEmpty(_jobId.Value),
                DurableMessagingJobOwnership.IsCompleted(_completedJobId.Value, ownershipId),
                _inboxDict.Count > 0);
            if (disposition == OwnershipMismatchDisposition.ReclaimOrphan)
            {
                LogOrphanedJobReclaimed(_logger, ownershipId, _grainContext.GrainId);
                _instruments.OnOrphanedJobReclaimed(_grainContext.GrainId.Type.ToString(), JobName);
                return DurableJobRunResult.Completed;
            }

            if (disposition == OwnershipMismatchDisposition.CompleteStale)
            {
                return DurableJobRunResult.Completed;
            }

            return DurableJobRunResult.InProgress(TimeSpan.FromMilliseconds(10));
        }

        if (!_recoveryCompleted)
        {
            return DurableJobRunResult.InProgress(TimeSpan.FromMilliseconds(10));
        }

        if (_localDrainJobIds.Contains(ownershipId))
        {
            return DurableJobRunResult.InProgress(TimeSpan.FromMilliseconds(10));
        }

        var key = new DurableMessagingPumpExecutionKey(JobName, context.Job.Id, context.RunId);
        if (_pumpResults.TryTake(key, out var result, out var exception))
        {
            if (exception is not null)
            {
                throw exception;
            }

            return result!;
        }

        if (_pumpResults.TryStart(key, cancellationToken, out var execution))
        {
            var state = new PumpTimerState(
                this,
                execution,
                ownershipId,
                hasStableOwnership,
                cancellationToken);
            state.Handle.Attach(_timerRegistry.RegisterGrainTimer(
                _grainContext,
                static (state, timerCancellation) => state.RunAsync(timerCancellation),
                state,
                new GrainTimerCreationOptions(TimeSpan.Zero, Timeout.InfiniteTimeSpan)
                {
                    Interleave = false,
                    KeepAlive = true
                }));
        }

        return DurableJobRunResult.InProgress(TimeSpan.FromMilliseconds(10));
    }

    private async Task RunPumpTimerAsync(
        DurableMessagingPumpExecution execution,
        string ownershipId,
        bool hasStableOwnership,
        CancellationToken jobCancellation,
        CancellationToken timerCancellation)
    {
        if (!_pumpResults.TryBegin(execution))
        {
            return;
        }

        DurableJobRunResult? result = null;
        Exception? failure = null;
        try
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                jobCancellation,
                timerCancellation,
                _shutdownCts.Token);
            result = await ExecuteJobCoreAsync(
                ownershipId,
                clearOwnershipWhenEmpty: true,
                hasStableOwnership,
                linkedCancellation.Token);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            if (failure is null)
            {
                _pumpResults.Complete(execution, result!);
            }
            else
            {
                _pumpResults.Fail(execution, failure);
            }
        }
    }

    internal async ValueTask<DurableJobRunResult> ExecuteJobCoreAsync(
        string jobId,
        bool clearOwnershipWhenEmpty,
        bool hasStableOwnership,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (!_recoveryCompleted)
            {
                return DurableJobRunResult.InProgress(TimeSpan.FromMilliseconds(10));
            }

            if (string.IsNullOrEmpty(_jobId.Value))
            {
                if (hasStableOwnership
                    && !DurableMessagingJobOwnership.IsCompleted(_completedJobId.Value, jobId))
                {
                    if (_inboxDict.Count == 0)
                    {
                        LogOrphanedJobReclaimed(_logger, jobId, _grainContext.GrainId);
                        _instruments.OnOrphanedJobReclaimed(_grainContext.GrainId.Type.ToString(), JobName);
                        return DurableJobRunResult.Completed;
                    }

                    return DurableJobRunResult.InProgress(TimeSpan.FromMilliseconds(10));
                }
                else if (GetDurableInboxCount() == 0)
                {
                    return DurableJobRunResult.Completed;
                }
                else
                {
                    _jobId.Value = jobId;
                    await _stateManager.WriteStateAsync(cancellationToken).ConfigureAwait(true);
                }
            }
            else if (!string.Equals(_jobId.Value, jobId, StringComparison.Ordinal))
            {
                return DurableJobRunResult.Completed;
            }
        }
        finally
        {
            _gate.Release();
        }

        await ProcessPendingMessagesAsync(cancellationToken).ConfigureAwait(true);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (!string.Equals(_jobId.Value, jobId, StringComparison.Ordinal))
            {
                return DurableJobRunResult.Completed;
            }

            CompactProcessedMessages();
            if (GetDurableInboxCount() == 0)
            {
                if (_inboxDict.Count > 0)
                {
                    return DurableJobRunResult.InProgress(TimeSpan.FromMilliseconds(10));
                }

                if (!clearOwnershipWhenEmpty)
                {
                    return DurableJobRunResult.Completed;
                }

                _completedJobId.Value = jobId;
                _jobId.Value = null;
                try
                {
                    await _stateManager.WriteStateAsync(cancellationToken).ConfigureAwait(true);
                    return DurableJobRunResult.Completed;
                }
                catch
                {
                    await _stateManager.RevertPendingChangesAsync(CancellationToken.None).ConfigureAwait(true);
                    return DurableJobRunResult.RescheduleAt(_jobTimeProvider.GetUtcNow() + _retryDelay);
                }
            }

            var nextAttempt = GetNextAttemptAt();
            var delay = nextAttempt - _timeProvider.GetUtcNow();
            return DurableJobRunResult.RescheduleAt(
                _jobTimeProvider.GetUtcNow() + (delay > TimeSpan.Zero ? delay : TimeSpan.Zero));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ProcessPendingMessagesAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var pending = _inboxDict
            .Where(pair =>
                !_provisionalAcceptances.Contains(pair.Key)
                && (!_messageStates.TryGetValue(pair.Key, out var state)
                    || state.NextAttemptAt is null
                    || state.NextAttemptAt <= now))
            .Take(_batchSize)
            .Select(static pair => pair.Value)
            .ToList();

        foreach (var envelope in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await ProcessMessageAsync(envelope, cancellationToken).ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogProcessingError(_logger, exception, envelope.MessageId, envelope.SenderId, envelope.RouteKey, envelope.CorrelationKey?.ToString());
            }
        }
    }

    private DateTimeOffset GetNextAttemptAt()
    {
        var now = _timeProvider.GetUtcNow();
        var attempts = _inboxDict.Keys
            .Select(key => _messageStates.TryGetValue(key, out var state) ? state.NextAttemptAt : null)
            .ToList();
        return attempts.Any(value => value is null || value <= now)
            ? now
            : attempts.Min()!.Value;
    }

    private int GetDurableInboxCount() =>
        _inboxDict.Keys.Count(key => !_provisionalAcceptances.Contains(key));

    private void CompactProcessedMessages()
    {
        var cutoff = _timeProvider.GetUtcNow() - _deduplicationWindow;
        foreach (var entry in _processed.Where(pair => pair.Value <= cutoff).ToList())
        {
            _processed.Remove(entry.Key);
        }
    }

    /// <summary>
    /// Processes a single message by invoking its handler.
    /// </summary>
    private async Task ProcessMessageAsync(DurableEnvelope envelope, CancellationToken cancellationToken)
    {
        var key = (envelope.SenderId, envelope.MessageId);
        var grainTypeName = _grainContext.GrainId.Type.ToString();
        var stopwatch = Stopwatch.StartNew();
        if (!_inboxDict.ContainsKey(key))
        {
            return;
        }

        var context = new InboxHandlerContext(envelope, _grainContext.GrainId, _outbox, _sessionPool);
        var stateGeneration = Volatile.Read(ref _stateGeneration);
        try
        {
            if (_durableInbox.TryFindHandler(context, out var handler))
            {
                Interlocked.Increment(ref _handlerExecutionDepth);
                try
                {
                    await handler.HandleAsync(context, cancellationToken).ConfigureAwait(true);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                finally
                {
                    Interlocked.Decrement(ref _handlerExecutionDepth);
                }
            }
            else
            {
                await DeadLetterAsync(key, envelope, "No compatible handler is registered.").ConfigureAwait(true);
                stopwatch.Stop();
                _instruments.OnInboxMessageProcessed(grainTypeName, envelope.RouteKey, "dead_lettered");
                _instruments.OnInboxProcessingDuration(stopwatch.Elapsed, grainTypeName, envelope.RouteKey);
                return;
            }

            if (Volatile.Read(ref _stateGeneration) != stateGeneration)
            {
                await _stateManager.RevertPendingChangesAsync(CancellationToken.None).ConfigureAwait(true);
                return;
            }

            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(true);
            try
            {
                if (_inboxDict.ContainsKey(key))
                {
                    RemoveMessage(key);
                    _messageStates.Remove(key);
                    _processed[key] = _timeProvider.GetUtcNow();
                    await _stateManager.WriteStateAsync(CancellationToken.None).ConfigureAwait(true);
                }

            }
            finally
            {
                _gate.Release();
            }

            stopwatch.Stop();
            _instruments.OnInboxMessageProcessed(grainTypeName, envelope.RouteKey, "success");
            _instruments.OnInboxProcessingDuration(stopwatch.Elapsed, grainTypeName, envelope.RouteKey);
            LogMessageProcessed(_logger, envelope.MessageId, envelope.SenderId, envelope.RouteKey, envelope.CorrelationKey?.ToString());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await _stateManager.RevertPendingChangesAsync(CancellationToken.None).ConfigureAwait(true);
            throw;
        }
        catch (Exception ex)
        {
            LogHandlerException(_logger, ex, envelope.MessageId, envelope.SenderId, envelope.RouteKey, envelope.CorrelationKey?.ToString());
            var deadLettered = await RecordProcessingFailureAsync(key, ex).ConfigureAwait(true);
            stopwatch.Stop();
            _instruments.OnInboxMessageProcessed(grainTypeName, envelope.RouteKey, deadLettered ? "dead_lettered" : "retry");
            _instruments.OnInboxProcessingDuration(stopwatch.Elapsed, grainTypeName, envelope.RouteKey);
        }
    }

    private void ValidateAcceptanceState(
        (GrainId SenderId, Guid MessageId) key,
        long expectedGeneration)
    {
        if (Volatile.Read(ref _stateGeneration) != expectedGeneration
            || !_inboxDict.ContainsKey(key)
            || string.IsNullOrEmpty(_jobId.Value))
        {
            throw new InvalidOperationException(
                "Durable inbox acceptance was interrupted by state recovery or deletion.");
        }
    }

    private async ValueTask<bool> RecordProcessingFailureAsync(
        (GrainId SenderId, Guid MessageId) key,
        Exception exception)
    {
        await _stateManager.RevertPendingChangesAsync(CancellationToken.None).ConfigureAwait(true);
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(true);
        try
        {
            if (!_inboxDict.TryGetValue(key, out var recoveredEnvelope))
            {
                return false;
            }

            if (!_messageStates.TryGetValue(key, out var state))
            {
                state = new InboxMessageState();
            }

            state.AttemptCount++;
            state.LastError = exception.ToString();
            if (state.AttemptCount >= _maxProcessingAttempts)
            {
                await DeadLetterUnderGateAsync(key, recoveredEnvelope, exception.Message, state.AttemptCount).ConfigureAwait(true);
                return true;
            }

            var exponent = Math.Min(
                state.AttemptCount - 1,
                DurableInboxOptions.MaximumBackoffExponent);
            state.NextAttemptAt = _timeProvider.GetUtcNow() + TimeSpan.FromTicks(_retryDelay.Ticks * (1L << exponent));
            _messageStates[key] = state;
            await _stateManager.WriteStateAsync(CancellationToken.None).ConfigureAwait(true);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask DeadLetterAsync(
        (GrainId SenderId, Guid MessageId) key,
        DurableEnvelope envelope,
        string reason)
    {
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(true);
        try
        {
            var attemptCount = _messageStates.TryGetValue(key, out var state) ? state.AttemptCount : 0;
            await DeadLetterUnderGateAsync(key, envelope, reason, attemptCount).ConfigureAwait(true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask DeadLetterUnderGateAsync(
        (GrainId SenderId, Guid MessageId) key,
        DurableEnvelope envelope,
        string reason,
        int attemptCount)
    {
        _deadLetters[key] = new InboxDeadLetter
        {
            Envelope = envelope,
            DeadLetteredAt = _timeProvider.GetUtcNow(),
            Reason = reason,
            AttemptCount = attemptCount
        };
        RemoveMessage(key);
        _messageStates.Remove(key);
        _processed[key] = _timeProvider.GetUtcNow();
        await _stateManager.WriteStateAsync(CancellationToken.None).ConfigureAwait(true);
    }

    internal async Task ResumeProcessingAsync(bool replaceExisting, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureMetricsActive();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            await EnsureJobScheduledUnderGateAsync(cancellationToken, replaceExisting: replaceExisting).ConfigureAwait(true);
            ScheduleLocalDrain();
        }

        finally
        {
            _gate.Release();
        }
    }

    public Task OnStart(CancellationToken cancellationToken) =>
        ResumeProcessingAsync(replaceExisting: true, cancellationToken);

    public Task OnStop(CancellationToken cancellationToken)
    {
        StopProcessing();
        return Task.CompletedTask;
    }

    internal void StopProcessing()
    {
        _shutdownCts.Cancel();
        if (Interlocked.Exchange(ref _metricsActive, 0) != 0)
        {
            _instruments.OnInboxDepthChanged(-Interlocked.Exchange(ref _reportedDepth, 0));
        }
    }

    public void Dispose()
    {
        StopProcessing();
        _shutdownCts.Dispose();
    }

    private void EnsureMetricsActive()
    {
        if (Interlocked.Exchange(ref _metricsActive, 1) == 0)
        {
            Volatile.Write(ref _reportedDepth, _inboxDict.Count);
            _instruments.OnInboxDepthChanged(_inboxDict.Count);
        }
    }

    private void UpdateInboxDepth(int delta)
    {
        if (Volatile.Read(ref _metricsActive) != 0)
        {
            Interlocked.Add(ref _reportedDepth, delta);
            _instruments.OnInboxDepthChanged(delta);
        }
    }

    private void ReconcileInboxDepth()
    {
        if (Volatile.Read(ref _metricsActive) == 0)
        {
            return;
        }

        var count = _inboxDict.Count;
        var delta = count - Interlocked.Exchange(ref _reportedDepth, count);
        if (delta != 0)
        {
            _instruments.OnInboxDepthChanged(delta);
        }
    }

    private bool RemoveMessage((GrainId SenderId, Guid MessageId) key)
    {
        if (!_inboxDict.Remove(key))
        {
            return false;
        }

        UpdateInboxDepth(-1);

        return true;
    }

    // Structured logging using LoggerMessage source generator

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Registered handler for route '{RouteKey}' on grain {GrainId}")]
    private static partial void LogHandlerRegistered(ILogger logger, string routeKey, GrainId grainId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Duplicate message {MessageId} from {SenderId} to {ReceiverId} on route '{RouteKey}' (CorrelationKey: {CorrelationKey})")]
    private static partial void LogDuplicateMessageDetected(ILogger logger, Guid messageId, GrainId senderId, GrainId receiverId, string routeKey, string? correlationKey);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Duplicate message {MessageId} from {SenderId} already in inbox for {ReceiverId} on route '{RouteKey}' (CorrelationKey: {CorrelationKey})")]
    private static partial void LogDuplicateMessageInInbox(ILogger logger, Guid messageId, GrainId senderId, GrainId receiverId, string routeKey, string? correlationKey);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Inbox at capacity ({Count}/{Capacity}) for grain {GrainId}, rejecting message {MessageId} from {SenderId} on route '{RouteKey}' (CorrelationKey: {CorrelationKey})")]
    private static partial void LogBackpressureRejection(ILogger logger, int count, int capacity, GrainId grainId, Guid messageId, GrainId senderId, string routeKey, string? correlationKey);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "No handler registered for route '{RouteKey}' on grain {GrainId}, rejecting message {MessageId} from {SenderId} (CorrelationKey: {CorrelationKey})")]
    private static partial void LogRouteNotFound(ILogger logger, string routeKey, GrainId grainId, Guid messageId, GrainId senderId, string? correlationKey);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Accepted message {MessageId} from {SenderId} to {ReceiverId} on route '{RouteKey}' (CorrelationKey: {CorrelationKey})")]
    private static partial void LogMessageAccepted(ILogger logger, Guid messageId, GrainId senderId, GrainId receiverId, string routeKey, string? correlationKey);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error processing message {MessageId} from {SenderId} on route '{RouteKey}' (CorrelationKey: {CorrelationKey})")]
    private static partial void LogProcessingError(ILogger logger, Exception exception, Guid messageId, GrainId senderId, string routeKey, string? correlationKey);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Handler for route '{RouteKey}' not found during processing of message {MessageId} (CorrelationKey: {CorrelationKey})")]
    private static partial void LogHandlerNotFoundDuringProcessing(ILogger logger, string routeKey, Guid messageId, string? correlationKey);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Processed message {MessageId} from {SenderId} on route '{RouteKey}' (CorrelationKey: {CorrelationKey})")]
    private static partial void LogMessageProcessed(ILogger logger, Guid messageId, GrainId senderId, string routeKey, string? correlationKey);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Handler threw exception for message {MessageId} from {SenderId} on route '{RouteKey}' (CorrelationKey: {CorrelationKey})")]
    private static partial void LogHandlerException(ILogger logger, Exception exception, Guid messageId, GrainId senderId, string routeKey, string? correlationKey);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Reclaimed orphaned inbox job ownership {OwnershipId} for grain {GrainId}")]
    private static partial void LogOrphanedJobReclaimed(ILogger logger, string ownershipId, GrainId grainId);

    private sealed class PumpTimerState(
        DurableInboxExtension owner,
        DurableMessagingPumpExecution execution,
        string ownershipId,
        bool hasStableOwnership,
        CancellationToken jobCancellation)
    {
        public OneShotTimerHandle Handle { get; } = new();

        public async Task RunAsync(CancellationToken timerCancellation)
        {
            try
            {
                await owner.RunPumpTimerAsync(
                    execution,
                    ownershipId,
                    hasStableOwnership,
                    jobCancellation,
                    timerCancellation);
            }
            finally
            {
                Handle.Complete();
            }
        }
    }

    private void ScheduleLocalDrain()
    {
        if (_jobId.Value is not { Length: > 0 } jobId || GetDurableInboxCount() == 0)
        {
            return;
        }

        if (!_localDrainJobIds.Add(jobId))
        {
            return;
        }

        var state = new LocalDrainTimerState(this, jobId);
        state.Handle.Attach(_timerRegistry.RegisterGrainTimer(
            _grainContext,
            static (state, cancellationToken) => state.RunAsync(cancellationToken),
            state,
            new GrainTimerCreationOptions(TimeSpan.Zero, Timeout.InfiniteTimeSpan)
            {
                Interleave = false,
                KeepAlive = true
            }));
    }

    private sealed class LocalDrainTimerState(DurableInboxExtension owner, string jobId)
    {
        public OneShotTimerHandle Handle { get; } = new();

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                _ = await owner.ExecuteJobCoreAsync(
                    jobId,
                    clearOwnershipWhenEmpty: false,
                    hasStableOwnership: false,
                    cancellationToken);
            }
            finally
            {
                owner._localDrainJobIds.Remove(jobId);
                Handle.Complete();
            }
        }
    }
}
