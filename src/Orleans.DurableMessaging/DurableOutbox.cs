using System;
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

namespace Orleans.DurableMessaging;

/// <summary>
/// Durable outbox implementation that inherits from <see cref="DurableDictionary{K,V}"/> and provides
/// background delivery capability.
/// Implements <see cref="ILifecycleObserver"/> to start pumping messages when the grain activates.
/// </summary>
/// <remarks>
/// <para>
/// This implementation uses a background task to pump messages from the outbox to target grains.
/// The pumping task is started when the grain activates (via lifecycle subscription) and is
/// also scheduled whenever messages become durable (via OnWriteCompleted callback from base class).
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
internal sealed partial class DurableOutbox : DurableDictionary<Guid, DurableEnvelope>, IDurableOutbox, IDurableJobFeatureHandler, ILifecycleObserver
{
    internal const string JobName = "orleans.messaging.outbox-flush";

    private readonly IJournaledStateManager _stateManager;
    private readonly IGrainFactory _grainFactory;
    private readonly IGrainContext _grainContext;
    private readonly ILogger<DurableOutbox> _logger;
    private readonly DurableMessagingInstruments _instruments;
    private readonly TimeSpan _backpressureRetryDelay;
    private readonly TimeSpan _maxRetryAge;
    private readonly int _maxDeliveryAttempts;
    private readonly int _batchSize;
    private readonly IDurableDictionary<Guid, OutboxMessageState> _messageStates;
    private readonly IDurableDictionary<Guid, OutboxDeadLetter> _deadLetters;
    private readonly IDurableValue<string> _jobId;
    private readonly ILocalDurableJobManager _jobManager;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _deliveryGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>
    /// Set of message IDs that have been added to the outbox but not yet durably persisted.
    /// Messages in this set will be skipped by the delivery pump until they become durable.
    /// </summary>
    private readonly HashSet<Guid> _pendingMessageIds = [];

    private int _metricsActive;

    /// <summary>
    /// Creates a new DurableOutbox instance.
    /// </summary>
    /// <param name="key">The keyed service key for state machine registration.</param>
    /// <param name="manager">State machine manager for durable storage.</param>
    /// <param name="shared">Shared journaled state manager services.</param>
    /// <param name="serviceProvider">The service provider.</param>
    /// <param name="grainFactory">Grain factory for accessing target grains.</param>
    /// <param name="grainContext">The grain context for lifecycle subscription.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="instruments">Journaling metrics.</param>
    /// <param name="options">Durable inbox options containing backpressure retry delay.</param>
    public DurableOutbox(
        [ServiceKey] string key,
        IJournaledStateManager manager,
        JournaledStateManagerShared shared,
        IServiceProvider serviceProvider,
        IGrainFactory grainFactory,
        IGrainContext grainContext,
        ILogger<DurableOutbox> logger,
        DurableMessagingInstruments instruments,
        [FromKeyedServices("outbox-message-state")] IDurableDictionary<Guid, OutboxMessageState> messageStates,
        [FromKeyedServices("outbox-dead-letters")] IDurableDictionary<Guid, OutboxDeadLetter> deadLetters,
        [FromKeyedServices("outbox-job-id")] IDurableValue<string> jobId,
        ILocalDurableJobManager jobManager,
        IDurableJobHandlerRegistry jobHandlers,
        TimeProvider timeProvider,
        IOptions<DurableInboxOptions> options)
        : base(key, manager, shared, serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(grainFactory);
        ArgumentNullException.ThrowIfNull(grainContext);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(instruments);
        ArgumentNullException.ThrowIfNull(messageStates);
        ArgumentNullException.ThrowIfNull(deadLetters);
        ArgumentNullException.ThrowIfNull(jobId);
        ArgumentNullException.ThrowIfNull(jobManager);
        ArgumentNullException.ThrowIfNull(jobHandlers);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);

        _stateManager = manager;
        _grainFactory = grainFactory;
        _grainContext = grainContext;
        _logger = logger;
        _instruments = instruments;
        _messageStates = messageStates;
        _deadLetters = deadLetters;
        _jobId = jobId;
        _jobManager = jobManager;
        _timeProvider = timeProvider;
        _backpressureRetryDelay = options.Value.BackpressureRetryDelay;
        _maxRetryAge = options.Value.MaxOutboxRetryAge;
        _maxDeliveryAttempts = options.Value.MaxDeliveryAttempts;
        _batchSize = options.Value.OutboxBatchSize;
        jobHandlers.Register(JobName, this);

        // Subscribe to the grain lifecycle to start pumping on activation
        var lifecycle = grainContext.ObservableLifecycle;
        lifecycle.Subscribe(RuntimeTypeNameFormatter.Format(GetType()), GrainLifecycleStage.Activate, this);
    }

    /// <summary>
    /// Gets all pending outbound messages (no ordering guarantee).
    /// </summary>
    public IEnumerable<DurableEnvelope> Messages => Values;

    /// <summary>
    /// Enqueues a fully-built envelope for delivery (non-generic).
    /// </summary>
    /// <param name="envelope">The envelope to send.</param>
    /// <remarks>
    /// The message is persisted atomically with grain state when IStateMachineManager.WriteStateAsync()
    /// is called. The background pump will deliver the message to the target grain ONLY AFTER
    /// the message has been durably persisted.
    /// </remarks>
    public void Send(DurableEnvelope envelope)
    {
        EnsureMetricsActive();
        var isNewMessage = !ContainsKey(envelope.MessageId);

        // Track this message as pending (not yet durable)
        _pendingMessageIds.Add(envelope.MessageId);

        // Store envelope keyed by MessageId for O(1) lookup during removal
        this[envelope.MessageId] = envelope;
        if (isNewMessage)
        {
            _messageStates[envelope.MessageId] = new OutboxMessageState();
            _instruments.OnOutboxDepthChanged(1);
        }

        // Record metric for message sent
        var grainType = _grainContext.GrainId.Type.ToString();
        _instruments.OnOutboxMessageSent(grainType, envelope.RouteKey);

        // NOTE: We do NOT call EnsurePumpScheduled() here. The pump will be scheduled
        // when OnWriteCompleted is called, which happens after WriteStateAsync() completes.
        // This ensures we only send messages that are durably persisted.
    }

    /// <summary>
    /// Called when the outbox dictionary's pending writes have been durably persisted.
    /// This is when we schedule the pump to deliver the now-durable messages.
    /// </summary>
    protected override void OnWriteCompleted()
    {
        // Clear the pending set - all messages in the outbox are now durable
        _pendingMessageIds.Clear();

        // Schedule a durable job to deliver the durable messages.
        if (Count > 0)
        {
            _ = EnsureJobScheduledAsync();
        }
    }

    /// <summary>
    /// Removes a message after successful delivery.
    /// </summary>
    /// <param name="messageId">The unique identifier of the message to remove.</param>
    /// <returns>True if the message was found and removed; otherwise, false.</returns>
    /// <remarks>
    /// Note: We do NOT dispose the envelope's ArcBuffer here because the envelope has been
    /// delivered to the receiver. Due to [Immutable] marking on DurableEnvelope/DurableEnvelopeData,
    /// Orleans may share the reference (especially for local calls), so the receiver still needs
    /// the buffer to be valid. The receiver is responsible for disposing after processing.
    /// </remarks>
    public bool RemoveMessage(Guid messageId)
    {
        _pendingMessageIds.Remove(messageId);
        _messageStates.Remove(messageId);
        var removed = Remove(messageId);
        if (removed && Volatile.Read(ref _metricsActive) != 0)
        {
            _instruments.OnOutboxDepthChanged(-1);
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
        return TryGetValue(messageId, out envelope);
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

            var now = _timeProvider.GetUtcNow();
            var pending = Values
                .Where(envelope =>
                    !_pendingMessageIds.Contains(envelope.MessageId)
                    && (!_messageStates.TryGetValue(envelope.MessageId, out var state)
                        || state.NextAttemptAt is null
                        || state.NextAttemptAt <= now))
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
            var stateChanged = false;

            foreach (var envelope in pending)
            {
                var stopwatch = Stopwatch.StartNew();
                try
                {
                    var targetGrain = _grainFactory.GetGrain<IDurableInboxExtension>(envelope.ReceiverId);
                    var result = await targetGrain.DeliverAsync(
                        envelope,
                        new DeliveryOptions { PollTimeout = TimeSpan.Zero },
                        cancellationToken).ConfigureAwait(true);

                    stopwatch.Stop();
                    switch (result.Status)
                    {
                        case DeliveryStatus.Accepted:
                        case DeliveryStatus.Duplicate:
                        case DeliveryStatus.Processed:
                            RemoveMessage(envelope.MessageId);
                            stateChanged = true;
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
                            RecordDeliveryFailure(envelope, "The receiver is backpressured.");
                            stateChanged = true;
                            backpressuredCount++;
                            LogDeliveryBackpressured(_logger, envelope.MessageId, envelope.ReceiverId, envelope.RouteKey, envelope.CorrelationKey?.ToString());
                            _instruments.OnOutboxMessageDelivered(grainTypeName, envelope.RouteKey, "backpressured");
                            break;
                        case DeliveryStatus.RouteNotFound:
                            RecordDeliveryFailure(envelope, result.Message ?? "The receiver has no compatible route.");
                            stateChanged = true;
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
                            RecordDeliveryFailure(envelope, $"Unexpected delivery status '{result.Status}'.");
                            stateChanged = true;
                            failedCount++;
                            LogUnexpectedDeliveryStatus(_logger, result.Status, envelope.MessageId, envelope.RouteKey, envelope.CorrelationKey?.ToString());
                            break;
                    }

                    _instruments.OnOutboxDeliveryDuration(stopwatch.Elapsed, grainTypeName, envelope.RouteKey);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    stopwatch.Stop();
                    RecordDeliveryFailure(envelope, ex.ToString());
                    stateChanged = true;
                    failedCount++;
                    LogDeliveryError(_logger, ex, envelope.MessageId, envelope.SenderId, envelope.ReceiverId, envelope.RouteKey, envelope.CorrelationKey?.ToString());
                    _instruments.OnOutboxMessageDelivered(grainTypeName, envelope.RouteKey, "error");
                    _instruments.OnOutboxDeliveryDuration(stopwatch.Elapsed, grainTypeName, envelope.RouteKey);
                }
            }

            if (stateChanged)
            {
                await _stateManager.WriteStateAsync(cancellationToken).ConfigureAwait(true);
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
        var now = _timeProvider.GetUtcNow();
        if (state.AttemptCount >= _maxDeliveryAttempts || now - envelope.CreatedAt >= _maxRetryAge)
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
            await EnsureJobScheduledAsync().ConfigureAwait(true);
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
            _instruments.OnOutboxDepthChanged(-Count);
        }

        return Task.CompletedTask;
    }

    private void EnsureMetricsActive()
    {
        if (Interlocked.Exchange(ref _metricsActive, 1) == 0)
        {
            _instruments.OnOutboxDepthChanged(Count);
        }
    }

    private async Task EnsureJobScheduledAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await _gate.WaitAsync(_shutdown.Token).ConfigureAwait(true);
                try
                {
                    if (Count - _pendingMessageIds.Count <= 0 || !string.IsNullOrEmpty(_jobId.Value))
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
                        _shutdown.Token).ConfigureAwait(true);

                    if (string.IsNullOrEmpty(_jobId.Value))
                    {
                        _jobId.Value = job.Id;
                        await _stateManager.WriteStateAsync(_shutdown.Token).ConfigureAwait(true);
                    }
                    return;
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogPumpLoopError(_logger, exception);
                await Task.Delay(_backpressureRetryDelay, _shutdown.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    public async ValueTask<DurableJobRunResult> ExecuteJobAsync(IJobRunContext context, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (string.IsNullOrEmpty(_jobId.Value))
            {
                if (Count == 0)
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

        await DeliverPendingMessagesAsync(cancellationToken).ConfigureAwait(true);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (!string.Equals(_jobId.Value, context.Job.Id, StringComparison.Ordinal))
            {
                return DurableJobRunResult.Completed;
            }

            if (Count == 0)
            {
                _jobId.Value = null;
                await _stateManager.WriteStateAsync(cancellationToken).ConfigureAwait(true);
                return DurableJobRunResult.Completed;
            }

            var now = _timeProvider.GetUtcNow();
            var nextAttempt = Values
                .Select(envelope => _messageStates.TryGetValue(envelope.MessageId, out var state) ? state.NextAttemptAt : null)
                .Where(static value => value.HasValue)
                .Min() ?? now;
            return DurableJobRunResult.RescheduleAt(nextAttempt <= now ? now : nextAttempt);
        }
        finally
        {
            _gate.Release();
        }
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
}
