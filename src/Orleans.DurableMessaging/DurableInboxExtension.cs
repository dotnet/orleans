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

namespace Orleans.DurableMessaging;

/// <summary>
/// Implementation of durable inbox extension for grain message delivery.
/// Handles message persistence, deduplication, long-polling, and processing.
/// </summary>
internal sealed partial class DurableInboxExtension :
    IDurableInboxExtension,
    IDurableJobFeatureHandler,
    IDisposable
{
    internal const string JobName = "orleans.messaging.inbox-drain";
    private const string TurnIsolationRequestContextKey = "Orleans.DurableJobs.TurnIsolation";

    private readonly IGrainContext _grainContext;
    private readonly IJournaledStateManager _stateManager;
    private readonly SerializerSessionPool _sessionPool;
    private readonly ILogger<DurableInboxExtension> _logger;
    private readonly DurableMessagingInstruments _instruments;
    private readonly IDurableInbox _durableInbox;
    private readonly IDictionary<(GrainId SenderId, Guid MessageId), DurableEnvelope> _inboxDict;
    private readonly IDurableDictionaryOwnership<(GrainId SenderId, Guid MessageId)> _inboxOwnership;
    private readonly IDictionary<(GrainId SenderId, Guid MessageId), DateTimeOffset> _processed;
    private readonly IDictionary<(GrainId SenderId, Guid MessageId), InboxMessageState> _messageStates;
    private readonly IDictionary<(GrainId SenderId, Guid MessageId), InboxDeadLetter> _deadLetters;
    private readonly IDurableValue<string> _jobId;
    private readonly IDurableOutbox _outbox;
    private readonly ILocalDurableJobManager _jobManager;
    private readonly TimeProvider _timeProvider;
    private readonly int _maxCapacity;
    private readonly TimeSpan _deduplicationWindow;
    private readonly int _maxProcessingAttempts;
    private readonly int _batchSize;
    private readonly TimeSpan _retryDelay;
    private readonly bool _enableLongPolling;
    private readonly TimeSpan _defaultPollTimeout;
    private readonly DurableMessagingCommitCoordinator _commitCoordinator;
    private readonly IDurableInboxFaultInjector? _faultInjector;
    private readonly DurableJobTurnIsolation _turnIsolation;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly object _processingLock = new();
    private readonly Dictionary<(GrainId SenderId, Guid MessageId), Task<DeliveryResult>> _processing = [];
    private int _metricsActive;
    private int _admissionStopped;
    private int _shutdownDisposed;

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
        IJournaledStateManager stateManager,
        SerializerSessionPool sessionPool,
        ILogger<DurableInboxExtension> logger,
        DurableMessagingInstruments instruments,
        IDurableInbox durableInbox,
        IDictionary<(GrainId SenderId, Guid MessageId), DurableEnvelope> inboxDict,
        IDictionary<(GrainId SenderId, Guid MessageId), DateTimeOffset> processed,
        IDictionary<(GrainId SenderId, Guid MessageId), InboxMessageState> messageStates,
        IDictionary<(GrainId SenderId, Guid MessageId), InboxDeadLetter> deadLetters,
        IDurableValue<string> jobId,
        IDurableOutbox outbox,
        ILocalDurableJobManager jobManager,
        IDurableJobHandlerRegistry jobHandlers,
        TimeProvider timeProvider,
        DurableInboxOptions options,
        DurableMessagingCommitCoordinator? commitCoordinator = null,
        IDurableInboxFaultInjector? faultInjector = null,
        DurableJobTurnIsolation? turnIsolation = null)
    {
        ArgumentNullException.ThrowIfNull(grainContext);
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
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(jobManager);
        ArgumentNullException.ThrowIfNull(jobHandlers);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);

        _grainContext = grainContext;
        _stateManager = stateManager;
        _sessionPool = sessionPool;
        _logger = logger;
        _instruments = instruments;
        _durableInbox = durableInbox;
        _inboxDict = inboxDict;
        _inboxOwnership = inboxDict as IDurableDictionaryOwnership<(GrainId SenderId, Guid MessageId)>
            ?? throw new InvalidOperationException("The durable inbox dictionary must support ownership transfer.");
        _processed = processed;
        _messageStates = messageStates;
        _deadLetters = deadLetters;
        _jobId = jobId;
        _outbox = outbox;
        _jobManager = jobManager;
        _timeProvider = timeProvider;
        _maxCapacity = options.MaxCapacity;
        _deduplicationWindow = options.DeduplicationWindow;
        _maxProcessingAttempts = options.MaxProcessingAttempts;
        _batchSize = options.InboxBatchSize;
        _retryDelay = options.BackpressureRetryDelay;
        _enableLongPolling = options.EnableLongPolling;
        _defaultPollTimeout = options.DefaultPollTimeout;
        _commitCoordinator = commitCoordinator ?? new DurableMessagingCommitCoordinator();
        _faultInjector = faultInjector;
        _turnIsolation = turnIsolation ?? new DurableJobTurnIsolation();
        jobHandlers.Register(this, requiresTurnIsolation: true);
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
    /// Supports long-polling: if PollTimeout &gt; 0, waits for processing before returning.
    /// </summary>
    /// <param name="envelope">The message envelope.</param>
    /// <param name="options">Delivery options including poll timeout.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result indicating delivery/processing status.</returns>
    public async ValueTask<DeliveryResult> DeliverAsync(
        DurableEnvelope envelope,
        DeliveryOptions options,
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _admissionStopped) != 0)
        {
            envelope.Data.Dispose();
            return DeliveryResult.Backpressured();
        }

        EnsureMetricsActive();
        var key = (envelope.SenderId, envelope.MessageId);
        var pollTimeout = options.PollTimeout == Timeout.InfiniteTimeSpan ? _defaultPollTimeout : options.PollTimeout;
        var shouldWait = false;
        DurableEnvelope? envelopeToProcess = null;
        Task<DeliveryResult>? processing = null;
        var result = DeliveryResult.Accepted();

        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(true);
        try
        {
            if (Volatile.Read(ref _admissionStopped) != 0)
            {
                envelope.Data.Dispose();
                return DeliveryResult.Backpressured();
            }

            if (_processed.ContainsKey(key))
            {
                LogDuplicateMessageDetected(
                    _logger,
                    envelope.MessageId,
                    envelope.SenderId,
                    envelope.ReceiverId,
                    envelope.RouteKey,
                    envelope.CorrelationKey?.ToString());
                _instruments.OnInboxMessageReceived(_grainContext.GrainId.Type.ToString(), envelope.RouteKey, "duplicate");
                envelope.Data.Dispose();
                return DeliveryResult.Duplicate();
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
                var storedEnvelope = _inboxDict[key];
                if (!ReferenceEquals(storedEnvelope.Data, envelope.Data))
                {
                    envelope.Data.Dispose();
                }

                await EnsureJobScheduledUnderGateAsync(CancellationToken.None).ConfigureAwait(true);
                result = DeliveryResult.Duplicate();
                shouldWait = _enableLongPolling && pollTimeout > TimeSpan.Zero;
                envelopeToProcess = storedEnvelope;
            }
            else
            {
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
                    envelope.Data.Dispose();
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
                    envelope.Data.Dispose();
                    return DeliveryResult.RouteNotFound(envelope.RouteKey);
                }

                _inboxDict[key] = envelope;
                _messageStates[key] = new InboxMessageState();
                _instruments.OnInboxDepthChanged(1);
                await _stateManager.WriteStateAsync(CancellationToken.None).ConfigureAwait(true);
                await EnsureJobScheduledUnderGateAsync(CancellationToken.None).ConfigureAwait(true);

                LogMessageAccepted(
                    _logger,
                    envelope.MessageId,
                    envelope.SenderId,
                    envelope.ReceiverId,
                    envelope.RouteKey,
                    envelope.CorrelationKey?.ToString());
                _instruments.OnInboxMessageReceived(_grainContext.GrainId.Type.ToString(), envelope.RouteKey, "accepted");
                shouldWait = _enableLongPolling && pollTimeout > TimeSpan.Zero;
                envelopeToProcess = envelope;
            }

            if (envelopeToProcess is { } admittedEnvelope)
            {
                processing = GetOrStartProcessing(admittedEnvelope);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (!shouldWait || processing is null)
        {
            return result;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(pollTimeout);
        try
        {
            return await processing.WaitAsync(timeout.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            return DeliveryResult.Pending();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
    }

    private async ValueTask EnsureJobScheduledUnderGateAsync(CancellationToken cancellationToken)
    {
        if (_inboxDict.Count == 0 || !string.IsNullOrEmpty(_jobId.Value))
        {
            return;
        }

        var job = await _jobManager.ScheduleJobAsync(
            new ScheduleJobRequest
            {
                Target = _grainContext.GrainId,
                JobName = JobName,
                DueTime = _timeProvider.GetUtcNow()
            },
            cancellationToken).ConfigureAwait(true);

        if (string.IsNullOrEmpty(_jobId.Value))
        {
            _jobId.Value = job.Id;
            await _stateManager.WriteStateAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    public bool CanHandle(string jobName) => string.Equals(jobName, JobName, StringComparison.Ordinal);

    public async ValueTask<DurableJobRunResult> ExecuteJobAsync(IJobRunContext context, CancellationToken cancellationToken)
    {
        try
        {
            return await ExecuteJobCoreAsync(context, cancellationToken).ConfigureAwait(true);
        }

        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogJobExecutionError(_logger, exception, context.Job.Id);
            return DurableJobRunResult.RescheduleAt(_timeProvider.GetUtcNow() + _retryDelay);
        }
    }

    private async ValueTask<DurableJobRunResult> ExecuteJobCoreAsync(IJobRunContext context, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _admissionStopped) != 0)
        {
            return DurableJobRunResult.Completed;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (string.IsNullOrEmpty(_jobId.Value))
            {
                if (_inboxDict.Count == 0)
                {
                    return DurableJobRunResult.Completed;
                }

                _jobId.Value = context.Job.Id;
                await _stateManager.WriteStateAsync(cancellationToken).ConfigureAwait(true);
            }
            else if (!string.Equals(_jobId.Value, context.Job.Id, StringComparison.Ordinal))
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
            if (!string.Equals(_jobId.Value, context.Job.Id, StringComparison.Ordinal))
            {
                return DurableJobRunResult.Completed;
            }

            CompactProcessedMessages();
            if (_inboxDict.Count == 0)
            {
                _jobId.Value = null;
                await _stateManager.WriteStateAsync(cancellationToken).ConfigureAwait(true);
                return DurableJobRunResult.Completed;
            }

            var nextAttempt = GetNextAttemptAt();
            return DurableJobRunResult.RescheduleAt(nextAttempt <= _timeProvider.GetUtcNow() ? _timeProvider.GetUtcNow() : nextAttempt);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ProcessPendingMessagesAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _admissionStopped) != 0)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        var pending = _inboxDict
            .Where(pair => !_messageStates.TryGetValue(pair.Key, out var state) || state.NextAttemptAt is null || state.NextAttemptAt <= now)
            .Take(_batchSize)
            .Select(static pair => pair.Value)
            .ToList();

        foreach (var envelope in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = await GetOrStartProcessing(envelope).WaitAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    private DateTimeOffset GetNextAttemptAt()
    {
        var now = _timeProvider.GetUtcNow();
        var next = _inboxDict.Keys
            .Select(key => _messageStates.TryGetValue(key, out var state) ? state.NextAttemptAt : null)
            .Where(static value => value.HasValue)
            .Min();
        return next ?? now;
    }

    private void CompactProcessedMessages()
    {
        var cutoff = _timeProvider.GetUtcNow() - _deduplicationWindow;
        foreach (var entry in _processed.Where(pair => pair.Value < cutoff).ToList())
        {
            _processed.Remove(entry.Key);
        }
    }

    /// <summary>
    /// Processes a single message by invoking its handler.
    /// </summary>
    private async Task<DeliveryResult> ProcessMessageAsync(DurableEnvelope envelope, CancellationToken cancellationToken)
    {
        using var turnLease = await _turnIsolation.EnterIsolatedAsync(cancellationToken).ConfigureAwait(true);
        turnLease.Activate();
        var key = (envelope.SenderId, envelope.MessageId);
        var grainTypeName = _grainContext.GrainId.Type.ToString();
        var stopwatch = Stopwatch.StartNew();
        var completionCommitted = false;
        if (!_inboxDict.ContainsKey(key))
        {
            return DeliveryResult.Duplicate();
        }

        var context = new InboxHandlerContext(envelope, _grainContext.GrainId, _outbox, _sessionPool);
        DurableMessagingCommitCoordinator.HandlerScope? commitScope = null;
        try
        {
            DeliveryResult completionResult;
            if (_durableInbox.TryFindHandler(context, out var handler))
            {
                commitScope = await _commitCoordinator.BeginHandlerAsync(cancellationToken).ConfigureAwait(true);
                commitScope.Activate();
                await InvokeHandlerAsync(handler, context, _sessionPool, cancellationToken).ConfigureAwait(true);
                cancellationToken.ThrowIfCancellationRequested();
                _faultInjector?.OnPhase(DurableInboxPersistencePhase.HandlerCompleted);
                completionResult = DeliveryResult.Processed();
            }
            else
            {
                await DeadLetterAsync(key, envelope, "No compatible handler is registered.", cancellationToken).ConfigureAwait(true);
                stopwatch.Stop();
                _instruments.OnInboxMessageProcessed(grainTypeName, envelope.RouteKey, "dead_lettered");
                _instruments.OnInboxProcessingDuration(stopwatch.Elapsed, grainTypeName, envelope.RouteKey);
                return DeliveryResult.DeadLettered("No compatible handler is registered.");
            }

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
            try
            {
                if (_inboxDict.ContainsKey(key))
                {
                    _faultInjector?.OnPhase(DurableInboxPersistencePhase.CompletionStaged);
                    RemoveMessage(key);
                    _messageStates.Remove(key);
                    _processed[key] = _timeProvider.GetUtcNow();
                    await CommitHandlerStateAsync(cancellationToken).ConfigureAwait(true);
                    completionCommitted = true;
                    commitScope?.Complete();
                    _faultInjector?.OnPhase(DurableInboxPersistencePhase.CompletionCommitted);
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
            return completionResult;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await RollbackHandlerStateAsync(
                ownsHandlerScope: commitScope is not null,
                cancellationToken: CancellationToken.None).ConfigureAwait(true);
            commitScope?.Dispose();
            commitScope = null;
            return DeliveryResult.Pending();
        }
        catch (Exception ex)
        {
            if (completionCommitted)
            {
                return DeliveryResult.Processed();
            }

            LogHandlerException(_logger, ex, envelope.MessageId, envelope.SenderId, envelope.RouteKey, envelope.CorrelationKey?.ToString());
            await RollbackHandlerStateAsync(
                ownsHandlerScope: commitScope is not null,
                cancellationToken: cancellationToken).ConfigureAwait(true);
            commitScope?.Dispose();
            commitScope = null;
            var deadLettered = await RecordProcessingFailureAsync(key, ex, cancellationToken).ConfigureAwait(true);
            stopwatch.Stop();
            _instruments.OnInboxMessageProcessed(grainTypeName, envelope.RouteKey, deadLettered ? "dead_lettered" : "retry");
            _instruments.OnInboxProcessingDuration(stopwatch.Elapsed, grainTypeName, envelope.RouteKey);
            if (deadLettered)
            {
                return DeliveryResult.DeadLettered(ex.Message);
            }

            return DeliveryResult.Pending();
        }
        finally
        {
            commitScope?.Dispose();
        }
    }

    private Task<DeliveryResult> GetOrStartProcessing(DurableEnvelope envelope)
    {
        var key = (envelope.SenderId, envelope.MessageId);
        lock (_processingLock)
        {
            if (_processing.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var task = ProcessMessageAsync(envelope, _shutdownCts.Token);
            _processing[key] = task;
            _ = task.ContinueWith(
                static (completed, state) =>
                {
                    var (owner, messageKey) = ((DurableInboxExtension, (GrainId, Guid)))state!;
                    lock (owner._processingLock)
                    {
                        if (owner._processing.TryGetValue(messageKey, out var current)
                            && ReferenceEquals(current, completed))
                        {
                            owner._processing.Remove(messageKey);
                        }
                    }
                },
                (this, key),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return task;
        }
    }

    private async ValueTask<bool> RecordProcessingFailureAsync(
        (GrainId SenderId, Guid MessageId) key,
        Exception exception,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
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
                await DeadLetterUnderGateAsync(key, recoveredEnvelope, exception.Message, state.AttemptCount, cancellationToken).ConfigureAwait(true);
                return true;
            }

            var exponent = Math.Min(state.AttemptCount - 1, 6);
            state.NextAttemptAt = _timeProvider.GetUtcNow() + TimeSpan.FromTicks(_retryDelay.Ticks * (1L << exponent));
            _messageStates[key] = state;
            await _stateManager.WriteStateAsync(cancellationToken).ConfigureAwait(true);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private ValueTask CommitHandlerStateAsync(CancellationToken cancellationToken) =>
        _stateManager is CoordinatedJournaledStateManager coordinated
            ? coordinated.CommitHandlerAsync(cancellationToken)
            : _stateManager.WriteStateAsync(cancellationToken);

    private ValueTask RollbackHandlerStateAsync(
        bool ownsHandlerScope,
        CancellationToken cancellationToken) =>
        ownsHandlerScope && _stateManager is CoordinatedJournaledStateManager coordinated
            ? coordinated.RollbackHandlerAsync(cancellationToken)
            : _stateManager.RevertPendingChangesAsync(cancellationToken);

    private async ValueTask DeadLetterAsync(
        (GrainId SenderId, Guid MessageId) key,
        DurableEnvelope envelope,
        string reason,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            var attemptCount = _messageStates.TryGetValue(key, out var state) ? state.AttemptCount : 0;
            await DeadLetterUnderGateAsync(key, envelope, reason, attemptCount, cancellationToken).ConfigureAwait(true);
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
        int attemptCount,
        CancellationToken cancellationToken)
    {
        _deadLetters[key] = new InboxDeadLetter
        {
            Envelope = envelope,
            DeadLetteredAt = _timeProvider.GetUtcNow(),
            Reason = reason,
            AttemptCount = attemptCount
        };
        RemoveMessage(key, disposeEnvelope: false);
        _messageStates.Remove(key);
        _processed[key] = _timeProvider.GetUtcNow();
        await _stateManager.WriteStateAsync(cancellationToken).ConfigureAwait(true);
    }

    private static async ValueTask InvokeHandlerAsync(
        IInboxHandler handler,
        IInboxHandlerContext context,
        SerializerSessionPool sessionPool,
        CancellationToken cancellationToken)
    {
        var previous = RequestContext.Entries.ToArray();
        var turnIsolationOwner = RequestContext.Get(TurnIsolationRequestContextKey);
        RequestContext.Clear();
        try
        {
            if (turnIsolationOwner is not null)
            {
                RequestContext.Set(TurnIsolationRequestContextKey, turnIsolationOwner);
            }

            if (context.Envelope.Data.HasContextKey(DurableEnvelopeBuilder.RequestContextKey))
            {
                if (!context.Envelope.Data.TryGetContextValue<Dictionary<string, object>>(
                    DurableEnvelopeBuilder.RequestContextKey,
                    out var values))
                {
                    throw new InvalidOperationException("The durable request context could not be deserialized.");
                }

                DurableEnvelopeBuilder.ValidateRequestContextValues(values, sessionPool);
                foreach (var (key, value) in values)
                {
                    if (!DurableEnvelopeBuilder.IsFrameworkContextKey(key))
                    {
                        RequestContext.Set(key, value);
                    }
                }
            }

            await handler.HandleAsync(context, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            RequestContext.Clear();
            foreach (var (key, value) in previous)
            {
                RequestContext.Set(key, value);
            }
        }
    }

    internal async Task ResumeProcessingAsync()
    {
        EnsureMetricsActive();
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(true);
        try
        {
            if (_inboxDict.Count > 0 && !string.IsNullOrEmpty(_jobId.Value))
            {
                _jobId.Value = null;
                await _stateManager.WriteStateAsync(CancellationToken.None).ConfigureAwait(true);
            }

            await EnsureJobScheduledUnderGateAsync(CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal void StopProcessing()
    {
        _ = Interlocked.Exchange(ref _admissionStopped, 1);
        if (Volatile.Read(ref _shutdownDisposed) == 0)
        {
            _shutdownCts.Cancel();
        }

        if (Interlocked.Exchange(ref _metricsActive, 0) != 0)
        {
            _instruments.OnInboxDepthChanged(-_inboxDict.Count);
        }
    }

    public async Task OnStop(CancellationToken cancellationToken = default)
    {
        StopProcessing();
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(true);
        _gate.Release();

        Task[] processing;
        lock (_processingLock)
        {
            processing = _processing.Values.ToArray();
        }

        await Task.WhenAll(processing).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        DisposeShutdownToken();
    }

    public void Dispose()
    {
        StopProcessing();
        lock (_processingLock)
        {
            if (_processing.Count == 0)
            {
                DisposeShutdownToken();
            }
        }
    }

    private void DisposeShutdownToken()
    {
        if (Interlocked.Exchange(ref _shutdownDisposed, 1) == 0)
        {
            _shutdownCts.Dispose();
        }
    }

    private void EnsureMetricsActive()
    {
        if (Interlocked.Exchange(ref _metricsActive, 1) == 0)
        {
            _instruments.OnInboxDepthChanged(_inboxDict.Count);
        }
    }

    private bool RemoveMessage(
        (GrainId SenderId, Guid MessageId) key,
        bool disposeEnvelope = true)
    {
        if (!_inboxOwnership.Remove(key, disposeEnvelope))
        {
            return false;
        }

        if (Volatile.Read(ref _metricsActive) != 0)
        {
            _instruments.OnInboxDepthChanged(-1);
        }

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
        Level = LogLevel.Error,
        Message = "Durable inbox job {JobId} failed and will be rescheduled")]
    private static partial void LogJobExecutionError(ILogger logger, Exception exception, string jobId);
}
