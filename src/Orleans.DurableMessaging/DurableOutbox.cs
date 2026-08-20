using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.DurableJobs;
using Orleans.DurableMessaging.Configuration;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization.TypeSystem;
using Orleans.Timers;

namespace Orleans.DurableMessaging;

/// <summary>
/// Durable outbox implementation which composes journaled dictionaries and provides background delivery capability.
/// Implements <see cref="ILifecycleObserver"/> to start pumping messages when the grain activates.
/// </summary>
/// <remarks>
/// <para>
/// This implementation uses a background task to pump messages from the outbox to target grains.
/// The pumping task is started when the grain activates (via lifecycle subscription) and is
/// also scheduled whenever messages become durable through journal commit notifications.
/// </para>
/// <para>
/// IMPORTANT: Messages are only sent AFTER they have been durably persisted. This ensures that
/// if the grain crashes after Send() but before WriteStateAsync() completes, the message won't
/// be lost and can be recovered and resent on reactivation.
/// </para>
/// <para>
/// Messages that fail due to backpressure remain in the outbox and will be retried by the
/// background pump. This design avoids blocking the grain for extended periods, maintaining
/// Orleans' non-blocking grain model.
/// </para>
/// </remarks>
internal sealed partial class DurableOutbox : IDurableOutbox, IDurableJobFeatureHandler, ILifecycleObserver, IJournaledStateObserver
{
    internal const string JobName = "orleans.messaging.outbox-flush";

    private readonly IJournaledStateManager _stateManager;
    private readonly IDurableDictionary<Guid, DurableEnvelope> _messages;
    private readonly IGrainFactory _grainFactory;
    private readonly IGrainContext _grainContext;
    private readonly ITimerRegistry _timerRegistry;
    private readonly ILogger<DurableOutbox> _logger;
    private readonly DurableMessagingInstruments _instruments;
    private readonly TimeSpan _backpressureRetryDelay;
    private readonly TimeSpan _maxRetryAge;
    private readonly int _maxDeliveryAttempts;
    private readonly int _batchSize;
    private readonly IDurableDictionary<Guid, OutboxMessageState> _messageStates;
    private readonly IDurableDictionary<Guid, OutboxDeadLetter> _deadLetters;
    private readonly IDurableValue<string> _jobId;
    private readonly IDurableValue<string> _completedJobId;
    private readonly IDurableValue<long> _jobSequence;
    private readonly ILocalDurableJobManager _jobManager;
    private readonly TimeProvider _jobTimeProvider;
    private readonly DurableMessagingPumpResults _pumpResults;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _deliveryGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>
    /// Set of message IDs that have been added to the outbox but not yet durably persisted.
    /// Messages in this set will be skipped by the delivery pump until they become durable.
    /// </summary>
    private readonly HashSet<Guid> _pendingMessageIds = [];
    private readonly HashSet<Guid> _committingMessageIds = [];
    private DateTimeOffset? _pendingJobDueTime;
    private bool _jobScheduleConfirmed;
    private bool _recoveryCompleted;

    private int _metricsActive;
    private int _reportedDepth;

    /// <summary>
    /// Creates a new DurableOutbox instance.
    /// </summary>
    /// <param name="manager">State manager for durable storage.</param>
    /// <param name="messages">Durable dictionary containing pending messages.</param>
    /// <param name="grainFactory">Grain factory for accessing target grains.</param>
    /// <param name="grainContext">The grain context for lifecycle subscription.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="instruments">Journaling metrics.</param>
    /// <param name="options">Durable inbox options containing backpressure retry delay.</param>
    public DurableOutbox(
        IJournaledStateManager manager,
        [FromKeyedServices("outbox")] IDurableDictionary<Guid, DurableEnvelope> messages,
        IGrainFactory grainFactory,
        IGrainContext grainContext,
        ITimerRegistry timerRegistry,
        ILogger<DurableOutbox> logger,
        DurableMessagingInstruments instruments,
        [FromKeyedServices("outbox-message-state")] IDurableDictionary<Guid, OutboxMessageState> messageStates,
        [FromKeyedServices("outbox-dead-letters")] IDurableDictionary<Guid, OutboxDeadLetter> deadLetters,
        [FromKeyedServices("outbox-job-id")] IDurableValue<string> jobId,
        [FromKeyedServices("outbox-completed-job-id")] IDurableValue<string> completedJobId,
        [FromKeyedServices("outbox-job-sequence")] IDurableValue<long> jobSequence,
        ILocalDurableJobManager jobManager,
        IDurableJobHandlerRegistry jobHandlers,
        DurableMessagingPumpResults pumpResults,
        [FromKeyedServices(DurableJobTimeProviderNames.DurableJobs)] TimeProvider jobTimeProvider,
        IOptions<DurableInboxOptions> options)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(grainFactory);
        ArgumentNullException.ThrowIfNull(grainContext);
        ArgumentNullException.ThrowIfNull(timerRegistry);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(instruments);
        ArgumentNullException.ThrowIfNull(messageStates);
        ArgumentNullException.ThrowIfNull(deadLetters);
        ArgumentNullException.ThrowIfNull(jobId);
        ArgumentNullException.ThrowIfNull(completedJobId);
        ArgumentNullException.ThrowIfNull(jobSequence);
        ArgumentNullException.ThrowIfNull(jobManager);
        ArgumentNullException.ThrowIfNull(jobHandlers);
        ArgumentNullException.ThrowIfNull(pumpResults);
        ArgumentNullException.ThrowIfNull(jobTimeProvider);
        ArgumentNullException.ThrowIfNull(options);
        DurableMessagingStateManagerCapabilities.Validate(manager);

        _stateManager = manager;
        _messages = messages;
        _grainFactory = grainFactory;
        _grainContext = grainContext;
        _timerRegistry = timerRegistry;
        _logger = logger;
        _instruments = instruments;
        _messageStates = messageStates;
        _deadLetters = deadLetters;
        _jobId = jobId;
        _completedJobId = completedJobId;
        _jobSequence = jobSequence;
        _jobManager = jobManager;
        _pumpResults = pumpResults;
        _jobTimeProvider = jobTimeProvider;
        _backpressureRetryDelay = options.Value.BackpressureRetryDelay;
        _maxRetryAge = options.Value.MaxOutboxRetryAge;
        _maxDeliveryAttempts = options.Value.MaxDeliveryAttempts;
        _batchSize = options.Value.OutboxBatchSize;
        jobHandlers.Register(JobName, this);
        manager.RegisterObserver(this);

        // Subscribe to the grain lifecycle to start pumping on activation
        var lifecycle = grainContext.ObservableLifecycle;
        lifecycle.Subscribe(RuntimeTypeNameFormatter.Format(GetType()), GrainLifecycleStage.Activate, this);
    }

    /// <summary>
    /// Gets all pending outbound messages (no ordering guarantee).
    /// </summary>
    public int Count => _messages.Count;

    /// <inheritdoc />
    public IEnumerable<DurableEnvelope> Messages => _messages.Values;

    /// <summary>
    /// Enqueues a fully-built envelope for delivery (non-generic).
    /// </summary>
    /// <param name="envelope">The envelope to send.</param>
    /// <remarks>
    /// The message is persisted atomically with grain state when
    /// <see cref="IJournaledStateManager.WriteStateAsync"/> is called. The background pump will
    /// deliver the message to the target grain ONLY AFTER the message has been durably persisted.
    /// </remarks>
    public void Send(DurableEnvelope envelope)
    {
        EnsureMetricsActive();
        if (_messages.TryGetValue(envelope.MessageId, out var existingEnvelope))
        {
            if (!AreEquivalent(existingEnvelope, envelope))
            {
                throw new InvalidOperationException(
                    $"The durable outbox already contains a different envelope with message ID '{envelope.MessageId}'.");
            }

            return;
        }

        var startsNewBatch = Count == 0 && _pendingMessageIds.Count == 0;

        // Track this message as pending (not yet durable)
        _pendingMessageIds.Add(envelope.MessageId);

        // Store envelope keyed by MessageId for O(1) lookup during removal
        _messages.Add(envelope.MessageId, envelope);
        if (startsNewBatch)
        {
            _jobId.Value = DurableMessagingJobOwnership.NextId(_jobSequence);
            _pendingJobDueTime = _jobTimeProvider.GetUtcNow();
            _jobScheduleConfirmed = false;
        }

        _messageStates[envelope.MessageId] = new OutboxMessageState
        {
            EnqueuedAt = _jobTimeProvider.GetUtcNow()
        };
        UpdateOutboxDepth(1);

        // Record metric for message sent
        var grainType = _grainContext.GrainId.Type.ToString();
        _instruments.OnOutboxMessageSent(grainType, envelope.RouteKey);

        // Durable scheduling is completed by OnWritePreparingAsync before this state can commit.
        // Delivery remains fenced by _pendingMessageIds until the commit completes.
    }

    private static bool AreEquivalent(DurableEnvelope left, DurableEnvelope right)
    {
        if (left.MessageId != right.MessageId
            || left.SenderId != right.SenderId
            || left.ReceiverId != right.ReceiverId
            || !string.Equals(left.RouteKey, right.RouteKey, StringComparison.Ordinal)
            || !Equals(left.CorrelationKey, right.CorrelationKey)
            || !Nullable.Equals(left.ReplyTo, right.ReplyTo)
            || left.CreatedAt != right.CreatedAt)
        {
            return false;
        }

        if (ReferenceEquals(left.Data, right.Data))
        {
            return true;
        }

        if (left.Data is null || right.Data is null
            || !SequenceEqual(left.Data.GetBodyBytes(), right.Data.GetBodyBytes()))
        {
            return false;
        }

        var leftContextKeys = left.Data.ContextKeys.ToHashSet(StringComparer.Ordinal);
        var rightContextKeys = right.Data.ContextKeys.ToHashSet(StringComparer.Ordinal);
        if (!leftContextKeys.SetEquals(rightContextKeys))
        {
            return false;
        }

        foreach (var key in leftContextKeys)
        {
            if (!left.Data.TryGetContextBytes(key, out var leftContext)
                || !right.Data.TryGetContextBytes(key, out var rightContext)
                || !SequenceEqual(leftContext, rightContext))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SequenceEqual(ReadOnlySequence<byte> left, ReadOnlySequence<byte> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        return left.IsSingleSegment && right.IsSingleSegment
            ? left.FirstSpan.SequenceEqual(right.FirstSpan)
            : left.ToArray().AsSpan().SequenceEqual(right.ToArray());
    }

    /// <summary>
    /// Called when the outbox dictionary's pending writes have been durably persisted.
    /// This is when we schedule the pump to deliver the now-durable messages.
    /// </summary>
    public void OnWriteStarted()
    {
        _committingMessageIds.Clear();
        _committingMessageIds.UnionWith(_pendingMessageIds);
    }

    public async ValueTask OnWritePreparingAsync(CancellationToken cancellationToken)
    {
        if (_pendingMessageIds.Count == 0 || _pendingJobDueTime is not { } dueTime || _jobScheduleConfirmed)
        {
            return;
        }

        var jobId = _jobId.Value;
        if (string.IsNullOrEmpty(jobId))
        {
            throw new InvalidOperationException("Pending outbox messages require stable durable job ownership.");
        }

        await _jobManager.ScheduleJobAsync(
            new ScheduleJobRequest
            {
                Target = _grainContext.GrainId,
                JobName = JobName,
                DueTime = dueTime,
                Metadata = DurableMessagingJobOwnership.CreateMetadata(jobId)
            },
            cancellationToken).ConfigureAwait(true);
        _jobScheduleConfirmed = true;
    }

    public void OnWriteCompleted()
    {
        _pendingMessageIds.ExceptWith(_committingMessageIds);
        if (_committingMessageIds.Count > 0)
        {
            _pendingJobDueTime = null;
        }

        _committingMessageIds.Clear();
    }

    public void OnRecoveryCompleted()
    {
        _recoveryCompleted = true;
        _pendingMessageIds.Clear();
        _committingMessageIds.Clear();
        _pendingJobDueTime = null;
        _jobScheduleConfirmed = false;
        ReconcileOutboxDepth();
        if (Count > 0)
        {
            QueueEnsureJobScheduled(replaceExisting: true);
        }
    }

    public void OnRecoveryStarted() => _recoveryCompleted = false;

    /// <summary>
    /// Removes a message after successful delivery.
    /// </summary>
    /// <param name="messageId">The unique identifier of the message to remove.</param>
    /// <returns>True if the message was found and removed; otherwise, false.</returns>
    public bool RemoveMessage(Guid messageId)
    {
        _pendingMessageIds.Remove(messageId);
        _messageStates.Remove(messageId);
        var removed = _messages.Remove(messageId);
        if (removed)
        {
            UpdateOutboxDepth(-1);
        }

        return removed;
    }

    /// <summary>
    /// Tries to get a specific outbox message.
    /// </summary>
    /// <param name="messageId">The unique identifier of the message.</param>
    /// <param name="envelope">When this method returns, contains the envelope if found; otherwise, the default value.</param>
    /// <returns>True if the message was found; otherwise, false.</returns>
    public bool TryGetMessage(Guid messageId, [MaybeNullWhen(false)] out DurableEnvelope envelope)
    {
        return _messages.TryGetValue(messageId, out envelope);
    }

    /// <summary>
    /// Triggers delivery of all durable pending messages in the outbox (single attempt).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the delivery operation.</returns>
    /// <remarks>
    /// This method makes a SINGLE attempt to deliver each durable pending message. Messages that
    /// are still pending (not yet durably persisted) are skipped. Messages that fail due to
    /// backpressure remain in the outbox and will be retried by the background pump.
    /// </remarks>
    public async Task DeliverPendingMessagesAsync(CancellationToken cancellationToken = default)
    {
        await _deliveryGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (Count == 0)
            {
                return;
            }

            var now = _jobTimeProvider.GetUtcNow();
            var pending = _messages.Values
                .Where(envelope =>
                    !_pendingMessageIds.Contains(envelope.MessageId)
                    && IsReadyForAttempt(envelope, now))
                .Take(_batchSize)
                .ToList();

            if (pending.Count == 0)
            {
                LogNoDurableMessages(_logger, Count);
                return;
            }

            LogDeliveringMessages(_logger, pending.Count);

            var grainTypeName = _grainContext.GrainId.Type.ToString();
            var deliveredCount = 0;
            var backpressuredCount = 0;
            var failedCount = 0;
            var batchDirty = false;

            try
            {
                foreach (var envelope in pending)
                {
                    var stopwatch = Stopwatch.StartNew();
                    var messageNow = _jobTimeProvider.GetUtcNow();
                    if (_messageStates.TryGetValue(envelope.MessageId, out var existingState)
                        && existingState.EnqueuedAt is { } enqueuedAt
                        && messageNow - enqueuedAt >= _maxRetryAge)
                    {
                        batchDirty = true;
                        DeadLetterExpiredMessage(envelope, existingState, messageNow);
                        failedCount++;
                        continue;
                    }

                    try
                    {
                        var targetGrain = _grainFactory.GetGrain<IDurableInboxExtension>(envelope.ReceiverId);
                        var result = await targetGrain.DeliverAsync(
                            envelope,
                            cancellationToken).ConfigureAwait(true);

                        stopwatch.Stop();
                        switch (result.Status)
                        {
                            case DeliveryStatus.Accepted:
                            case DeliveryStatus.Duplicate:
                                batchDirty = true;
                                RemoveMessage(envelope.MessageId);
                                deliveredCount++;
                                LogMessageDelivered(
                                    _logger,
                                    envelope.MessageId,
                                    envelope.SenderId,
                                    envelope.ReceiverId,
                                    envelope.RouteKey,
                                    result.Status,
                                    envelope.CorrelationKey?.ToString());
                                _instruments.OnOutboxMessageDelivered(grainTypeName, envelope.RouteKey, result.Status.ToString().ToLowerInvariant());
                                break;
                            case DeliveryStatus.Backpressured:
                                batchDirty = true;
                                RecordDeliveryFailure(envelope, "The receiver is backpressured.");
                                backpressuredCount++;
                                LogDeliveryBackpressured(_logger, envelope.MessageId, envelope.ReceiverId, envelope.RouteKey, envelope.CorrelationKey?.ToString());
                                _instruments.OnOutboxMessageDelivered(grainTypeName, envelope.RouteKey, "backpressured");
                                break;
                            case DeliveryStatus.RouteNotFound:
                                batchDirty = true;
                                RecordDeliveryFailure(envelope, result.Message ?? "The receiver has no compatible route.");
                                failedCount++;
                                LogDeliveryRouteNotFound(
                                    _logger,
                                    envelope.MessageId,
                                    envelope.SenderId,
                                    envelope.ReceiverId,
                                    envelope.RouteKey,
                                    envelope.CorrelationKey?.ToString(),
                                    result.Message ?? "(no message)");
                                _instruments.OnOutboxMessageDelivered(grainTypeName, envelope.RouteKey, "route_not_found");
                                break;
                            default:
                                batchDirty = true;
                                RecordDeliveryFailure(envelope, $"Unexpected delivery status '{result.Status}'.");
                                failedCount++;
                                LogUnexpectedDeliveryStatus(_logger, result.Status, envelope.MessageId, envelope.RouteKey, envelope.CorrelationKey?.ToString());
                                break;
                        }

                        _instruments.OnOutboxDeliveryDuration(stopwatch.Elapsed, grainTypeName, envelope.RouteKey);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        stopwatch.Stop();
                        batchDirty = true;
                        RecordDeliveryFailure(envelope, ex.ToString());
                        failedCount++;
                        LogDeliveryError(_logger, ex, envelope.MessageId, envelope.SenderId, envelope.ReceiverId, envelope.RouteKey, envelope.CorrelationKey?.ToString());
                        _instruments.OnOutboxMessageDelivered(grainTypeName, envelope.RouteKey, "error");
                        _instruments.OnOutboxDeliveryDuration(stopwatch.Elapsed, grainTypeName, envelope.RouteKey);
                    }
                }

                if (batchDirty)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await _stateManager.WriteStateAsync(CancellationToken.None).ConfigureAwait(true);
                    batchDirty = false;
                }
            }
            catch
            {
                if (batchDirty)
                {
                    await _stateManager.RevertPendingChangesAsync(CancellationToken.None).ConfigureAwait(true);
                }

                throw;
            }

            LogDeliveryComplete(_logger, deliveredCount, backpressuredCount, failedCount, Count);
        }
        finally
        {
            _deliveryGate.Release();
        }
    }

    private void RecordDeliveryFailure(DurableEnvelope envelope, string error)
    {
        if (!_messageStates.TryGetValue(envelope.MessageId, out var state))
        {
            state = new OutboxMessageState();
        }

        state.AttemptCount++;
        state.LastError = error;
        var now = _jobTimeProvider.GetUtcNow();
        state.EnqueuedAt ??= now;
        if (state.AttemptCount >= _maxDeliveryAttempts || now - state.EnqueuedAt.Value >= _maxRetryAge)
        {
            _deadLetters[envelope.MessageId] = new OutboxDeadLetter
            {
                Envelope = envelope,
                DeadLetteredAt = now,
                Reason = error,
                AttemptCount = state.AttemptCount
            };
            RemoveMessage(envelope.MessageId);
            return;
        }

        var exponent = Math.Min(state.AttemptCount - 1, 6);
        var delay = TimeSpan.FromTicks(_backpressureRetryDelay.Ticks * (1L << exponent));
        state.NextAttemptAt = now + delay;
        _messageStates[envelope.MessageId] = state;
    }

    private void DeadLetterExpiredMessage(
        DurableEnvelope envelope,
        OutboxMessageState state,
        DateTimeOffset now)
    {
        _deadLetters[envelope.MessageId] = new OutboxDeadLetter
        {
            Envelope = envelope,
            DeadLetteredAt = now,
            Reason = $"The message exceeded the maximum retry age of {_maxRetryAge}.",
            AttemptCount = state.AttemptCount
        };
        RemoveMessage(envelope.MessageId);
    }

    /// <summary>
    /// Called when the grain activates. Starts the background pump if there are pending durable messages.
    /// </summary>
    public async Task OnStart(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        EnsureMetricsActive();
        if (Count > 0)
        {
            LogPumpStartingOnActivation(_logger, Count);
            await EnsureJobScheduledAsync(replaceExisting: true, cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Called when the grain deactivates. Stops the background pump.
    /// </summary>
    public Task OnStop(CancellationToken cancellationToken = default)
    {
        _shutdown.Cancel();
        if (Interlocked.Exchange(ref _metricsActive, 0) != 0)
        {
            _instruments.OnOutboxDepthChanged(-Interlocked.Exchange(ref _reportedDepth, 0));
        }

        return Task.CompletedTask;
    }

    private void EnsureMetricsActive()
    {
        if (Interlocked.Exchange(ref _metricsActive, 1) == 0)
        {
            Volatile.Write(ref _reportedDepth, Count);
            _instruments.OnOutboxDepthChanged(Count);
        }
    }

    private void UpdateOutboxDepth(int delta)
    {
        if (Volatile.Read(ref _metricsActive) != 0)
        {
            Interlocked.Add(ref _reportedDepth, delta);
            _instruments.OnOutboxDepthChanged(delta);
        }
    }

    private void ReconcileOutboxDepth()
    {
        if (Volatile.Read(ref _metricsActive) == 0)
        {
            return;
        }

        var count = Count;
        var delta = count - Interlocked.Exchange(ref _reportedDepth, count);
        if (delta != 0)
        {
            _instruments.OnOutboxDepthChanged(delta);
        }
    }

    private void QueueEnsureJobScheduled(bool replaceExisting)
    {
        var state = new EnsureJobTimerState(this, replaceExisting);
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

    internal async Task EnsureJobScheduledAsync(bool replaceExisting, CancellationToken cancellationToken)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        var token = linkedCancellation.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await _gate.WaitAsync(token).ConfigureAwait(true);
                try
                {
                    if (Count - _pendingMessageIds.Count <= 0
                        || (!replaceExisting && !string.IsNullOrEmpty(_jobId.Value)))
                    {
                        return;
                    }

                    var persistOwnership = replaceExisting || string.IsNullOrEmpty(_jobId.Value);
                    var ownershipId = GetOrCreateOwnershipId(replaceExisting);
                    await _jobManager.ScheduleJobAsync(
                        new ScheduleJobRequest
                        {
                            Target = _grainContext.GrainId,
                            JobName = JobName,
                            DueTime = _pendingJobDueTime ?? _jobTimeProvider.GetUtcNow(),
                            Metadata = DurableMessagingJobOwnership.CreateMetadata(ownershipId)
                        },
                        token).ConfigureAwait(true);

                    if (persistOwnership)
                    {
                        await _stateManager.WriteStateAsync(token).ConfigureAwait(true);
                    }

                    _jobScheduleConfirmed = true;
                    _pendingJobDueTime = null;
                    return;
                }
                finally
                {
                    _gate.Release();
                }
            }

            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogPumpLoopError(_logger, exception);
                await Task.Delay(_backpressureRetryDelay, token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    private string GetOrCreateOwnershipId(bool replaceExisting)
    {
        if (string.IsNullOrEmpty(_jobId.Value)
            || (replaceExisting && _pendingJobDueTime is null))
        {
            _jobId.Value = DurableMessagingJobOwnership.NextId(_jobSequence);
            _pendingJobDueTime = _jobTimeProvider.GetUtcNow();
            _jobScheduleConfirmed = false;
        }

        return _jobId.Value!;
    }

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
                Count > 0);
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

            return DurableJobRunResult.PollAfter(TimeSpan.FromMilliseconds(10));
        }

        if (!_recoveryCompleted)
        {
            return DurableJobRunResult.PollAfter(TimeSpan.FromMilliseconds(10));
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

        return DurableJobRunResult.PollAfter(TimeSpan.FromMilliseconds(10));
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
                _shutdown.Token);
            result = await ExecuteJobCoreAsync(
                ownershipId,
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
        bool hasStableOwnership,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (!_recoveryCompleted)
            {
                return DurableJobRunResult.PollAfter(TimeSpan.FromMilliseconds(10));
            }

            if (string.IsNullOrEmpty(_jobId.Value))
            {
                if (hasStableOwnership
                    && !DurableMessagingJobOwnership.IsCompleted(_completedJobId.Value, jobId))
                {
                    if (Count == 0)
                    {
                        LogOrphanedJobReclaimed(_logger, jobId, _grainContext.GrainId);
                        _instruments.OnOrphanedJobReclaimed(_grainContext.GrainId.Type.ToString(), JobName);
                        return DurableJobRunResult.Completed;
                    }

                    return DurableJobRunResult.PollAfter(TimeSpan.FromMilliseconds(10));
                }
                else if (Count == 0)
                {
                    return DurableJobRunResult.Completed;
                }
                else
                {
                    _jobId.Value = jobId;
                    await _stateManager.WriteStateAsync(cancellationToken).ConfigureAwait(true);
                }

                return DurableJobRunResult.RetryAt(_jobTimeProvider.GetUtcNow() + TimeSpan.FromMilliseconds(10));
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

        await DeliverPendingMessagesAsync(cancellationToken).ConfigureAwait(true);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (!string.Equals(_jobId.Value, jobId, StringComparison.Ordinal))
            {
                return DurableJobRunResult.Completed;
            }

            if (Count == 0)
            {
                _completedJobId.Value = jobId;
                _jobId.Value = null;
                try
                {
                    await _stateManager.WriteStateAsync(cancellationToken).ConfigureAwait(true);
                    _jobScheduleConfirmed = false;
                    return DurableJobRunResult.Completed;
                }
                catch
                {
                    await _stateManager.RevertPendingChangesAsync(CancellationToken.None).ConfigureAwait(true);
                    return DurableJobRunResult.RetryAt(_jobTimeProvider.GetUtcNow() + _backpressureRetryDelay);
                }
            }

            if (_pendingMessageIds.Count > 0)
            {
                return DurableJobRunResult.PollAfter(TimeSpan.FromMilliseconds(10));
            }

            var now = _jobTimeProvider.GetUtcNow();
            var attempts = _messages.Values
                .Select(envelope => GetNextAttemptAt(envelope, now))
                .ToList();
            var nextAttempt = attempts.Any(value => value is null || value <= now)
                ? now
                : attempts.Min()!.Value;
            return DurableJobRunResult.RetryAt(nextAttempt <= now ? now : nextAttempt);
        }

        finally
        {
            _gate.Release();
        }
    }

    private bool IsReadyForAttempt(DurableEnvelope envelope, DateTimeOffset now)
    {
        if (!_messageStates.TryGetValue(envelope.MessageId, out var state))
        {
            return true;
        }

        return state.EnqueuedAt is { } enqueuedAt && now - enqueuedAt >= _maxRetryAge
            || state.NextAttemptAt is null
            || state.NextAttemptAt <= now;
    }

    private DateTimeOffset? GetNextAttemptAt(DurableEnvelope envelope, DateTimeOffset now)
    {
        if (!_messageStates.TryGetValue(envelope.MessageId, out var state))
        {
            return null;
        }

        var retryAt = state.NextAttemptAt ?? now;
        var expiresAt = state.EnqueuedAt is { } enqueuedAt ? enqueuedAt + _maxRetryAge : retryAt;
        return retryAt <= expiresAt ? retryAt : expiresAt;
    }

    // Structured logging using LoggerMessage source generator

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "No durable messages to deliver (all {Count} messages are still pending)")]
    private static partial void LogNoDurableMessages(ILogger logger, int count);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Delivering {Count} durable messages from outbox")]
    private static partial void LogDeliveringMessages(ILogger logger, int count);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Delivered message {MessageId} from {SenderId} to {ReceiverId} on route '{RouteKey}' (Status: {Status}, CorrelationKey: {CorrelationKey})")]
    private static partial void LogMessageDelivered(ILogger logger, Guid messageId, GrainId senderId, GrainId receiverId, string routeKey, DeliveryStatus status, string? correlationKey);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Route not found for message {MessageId} from {SenderId} to {ReceiverId} on route '{RouteKey}' (CorrelationKey: {CorrelationKey}): {Message}")]
    private static partial void LogDeliveryRouteNotFound(ILogger logger, Guid messageId, GrainId senderId, GrainId receiverId, string routeKey, string? correlationKey, string? message);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Backpressured delivering message {MessageId} to {ReceiverId} on route '{RouteKey}' (CorrelationKey: {CorrelationKey}), will retry later")]
    private static partial void LogDeliveryBackpressured(ILogger logger, Guid messageId, GrainId receiverId, string routeKey, string? correlationKey);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Unexpected delivery status {Status} for message {MessageId} on route '{RouteKey}' (CorrelationKey: {CorrelationKey})")]
    private static partial void LogUnexpectedDeliveryStatus(ILogger logger, DeliveryStatus status, Guid messageId, string routeKey, string? correlationKey);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error delivering message {MessageId} from {SenderId} to {ReceiverId} on route '{RouteKey}' (CorrelationKey: {CorrelationKey})")]
    private static partial void LogDeliveryError(ILogger logger, Exception exception, Guid messageId, GrainId senderId, GrainId receiverId, string routeKey, string? correlationKey);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Outbox delivery complete: {DeliveredCount} delivered, {BackpressuredCount} backpressured, {FailedCount} failed, {RemainingCount} remaining")]
    private static partial void LogDeliveryComplete(ILogger logger, int deliveredCount, int backpressuredCount, int failedCount, int remainingCount);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Grain activated with {Count} pending outbox messages, starting pump")]
    private static partial void LogPumpStartingOnActivation(ILogger logger, int count);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error in outbox pump loop")]
    private static partial void LogPumpLoopError(ILogger logger, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Reclaimed orphaned outbox job ownership {OwnershipId} for grain {GrainId}")]
    private static partial void LogOrphanedJobReclaimed(ILogger logger, string ownershipId, GrainId grainId);

    private sealed class PumpTimerState(
        DurableOutbox owner,
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

    private sealed class EnsureJobTimerState(DurableOutbox owner, bool replaceExisting)
    {
        public OneShotTimerHandle Handle { get; } = new();

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                await owner.EnsureJobScheduledAsync(replaceExisting, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                LogPumpLoopError(owner._logger, exception);
            }
            finally
            {
                Handle.Complete();
            }
        }
    }
}
