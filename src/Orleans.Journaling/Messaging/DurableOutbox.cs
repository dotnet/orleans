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
using Orleans.Journaling.Configuration;
using Orleans.Runtime;
using Orleans.Serialization.TypeSystem;

namespace Orleans.Journaling.Messaging;

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
internal sealed partial class DurableOutbox : DurableDictionary<Guid, DurableEnvelope>, IDurableOutbox, ILifecycleObserver
{
    private readonly IGrainFactory _grainFactory;
    private readonly IGrainContext _grainContext;
    private readonly ILogger<DurableOutbox> _logger;
    private readonly JournalingInstruments _instruments;
    private readonly TimeSpan _backpressureRetryDelay;
    private readonly CancellationTokenSource _shutdownCts = new();

    /// <summary>
    /// Set of message IDs that have been added to the outbox but not yet durably persisted.
    /// Messages in this set will be skipped by the delivery pump until they become durable.
    /// </summary>
    private readonly HashSet<Guid> _pendingMessageIds = [];

    private Task? _pumpTask;
    private readonly object _pumpLock = new();

    /// <summary>
    /// Counter incremented when messages become durable. The pump loop checks this
    /// to determine if new work arrived while it was processing.
    /// </summary>
    private int _pumpVersion;
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
        JournalingInstruments instruments,
        IOptions<DurableInboxOptions> options)
        : base(key, manager, shared, serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(grainFactory);
        ArgumentNullException.ThrowIfNull(grainContext);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(instruments);
        ArgumentNullException.ThrowIfNull(options);

        _grainFactory = grainFactory;
        _grainContext = grainContext;
        _logger = logger;
        _instruments = instruments;
        _backpressureRetryDelay = options.Value.BackpressureRetryDelay;

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

        // Schedule the pump to deliver the durable messages
        if (Count > 0)
        {
            EnsurePumpScheduled();
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
        if (Count == 0)
        {
            return;
        }

        // Snapshot pending messages and skip those not yet durable
        var pending = Values
            .Where(e => !_pendingMessageIds.Contains(e.MessageId))
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

        foreach (var envelope in pending)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                // Get the target grain's inbox extension
                var targetGrain = _grainFactory.GetGrain<IDurableInboxExtension>(envelope.ReceiverId);

                // Deliver with no long-polling (immediate return after persistence)
                var options = new DeliveryOptions { PollTimeout = TimeSpan.Zero };

                var result = await targetGrain.DeliverAsync(envelope, options, cancellationToken).ConfigureAwait(true);

                stopwatch.Stop();

                switch (result.Status)
                {
                    case DeliveryStatus.Accepted:
                    case DeliveryStatus.Duplicate:
                    case DeliveryStatus.Processed:
                        // Success - remove from outbox
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

                        // Record successful delivery metrics
                        var statusLabel = result.Status.ToString().ToLowerInvariant();
                        _instruments.OnOutboxMessageDelivered(grainTypeName, envelope.RouteKey, statusLabel);
                        _instruments.OnOutboxDeliveryDuration(stopwatch.Elapsed, grainTypeName, envelope.RouteKey);
                        break;

                    case DeliveryStatus.RouteNotFound:
                        // No handler for route - log but keep in outbox for retry
                        // (handler might be registered later)
                        LogDeliveryRouteNotFound(
                            _logger,
                            envelope.MessageId,
                            envelope.SenderId,
                            envelope.ReceiverId,
                            envelope.RouteKey,
                            envelope.CorrelationKey?.ToString(),
                            result.Message ?? "(no message)");
                        failedCount++;

                        // Record metric
                        _instruments.OnOutboxMessageDelivered(grainTypeName, envelope.RouteKey, "route_not_found");
                        _instruments.OnOutboxDeliveryDuration(stopwatch.Elapsed, grainTypeName, envelope.RouteKey);
                        break;

                    case DeliveryStatus.Backpressured:
                        // Leave in outbox - will be retried by background pump
                        backpressuredCount++;
                        LogDeliveryBackpressured(
                            _logger,
                            envelope.MessageId,
                            envelope.ReceiverId,
                            envelope.RouteKey,
                            envelope.CorrelationKey?.ToString());

                        // Record metric
                        _instruments.OnOutboxMessageDelivered(grainTypeName, envelope.RouteKey, "backpressured");
                        _instruments.OnOutboxDeliveryDuration(stopwatch.Elapsed, grainTypeName, envelope.RouteKey);
                        break;

                    default:
                        LogUnexpectedDeliveryStatus(
                            _logger,
                            result.Status,
                            envelope.MessageId,
                            envelope.RouteKey,
                            envelope.CorrelationKey?.ToString());
                        failedCount++;
                        break;
                }
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                failedCount++;
                LogDeliveryError(
                    _logger,
                    ex,
                    envelope.MessageId,
                    envelope.SenderId,
                    envelope.ReceiverId,
                    envelope.RouteKey,
                    envelope.CorrelationKey?.ToString());

                // Record error metric
                _instruments.OnOutboxMessageDelivered(grainTypeName, envelope.RouteKey, "error");
                _instruments.OnOutboxDeliveryDuration(stopwatch.Elapsed, grainTypeName, envelope.RouteKey);
            }
        }

        LogDeliveryComplete(_logger, deliveredCount, backpressuredCount, failedCount, Count);
    }

    /// <summary>
    /// Called when the grain activates. Starts the background pump if there are pending durable messages.
    /// </summary>
    public Task OnStart(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }

        EnsureMetricsActive();

        // On reactivation, all messages in the outbox are durable (they were persisted before deactivation)
        // The _pendingMessageIds set is empty since it's not persisted
        if (Count > 0)
        {
            LogPumpStartingOnActivation(_logger, Count);
            EnsurePumpScheduled();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Called when the grain deactivates. Stops the background pump.
    /// </summary>
    public Task OnStop(CancellationToken cancellationToken = default)
    {
        _shutdownCts.Cancel();
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

    /// <summary>
    /// Ensures the background pump task is scheduled to run.
    /// Thread-safe - can be called from multiple concurrent calls.
    /// </summary>
    private void EnsurePumpScheduled()
    {
        lock (_pumpLock)
        {
            // Increment version to signal new work
            Interlocked.Increment(ref _pumpVersion);

            // If no pump task is running, start one
            if (_pumpTask is null || _pumpTask.IsCompleted)
            {
                _pumpTask = PumpLoopAsync(_shutdownCts.Token);
            }
        }
    }

    /// <summary>
    /// Background loop that pumps messages from the outbox.
    /// Uses version-based coordination to avoid race conditions.
    /// </summary>
    private async Task PumpLoopAsync(CancellationToken cancellationToken)
    {
        // Yield to allow the caller to continue before we start pumping
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.ForceYielding);

        while (!cancellationToken.IsCancellationRequested)
        {
            // Capture current version INSIDE the lock to avoid races with EnsurePumpScheduled
            int versionBeforePump;
            lock (_pumpLock)
            {
                versionBeforePump = Volatile.Read(ref _pumpVersion);
            }

            // Check if there are any durable messages to deliver
            var durableCount = Count - _pendingMessageIds.Count;
            if (durableCount <= 0)
            {
                // Check if we should exit - must hold lock to coordinate with EnsurePumpScheduled
                lock (_pumpLock)
                {
                    var currentVersion = Volatile.Read(ref _pumpVersion);

                    // If version changed, new work was added - continue processing
                    if (currentVersion != versionBeforePump)
                    {
                        continue;
                    }

                    // If there are still no durable messages, safe to exit
                    durableCount = Count - _pendingMessageIds.Count;
                    if (durableCount <= 0)
                    {
                        // Set _pumpTask to completed BEFORE returning to ensure
                        // any concurrent EnsurePumpScheduled sees IsCompleted=true
                        _pumpTask = Task.CompletedTask;
                        return;
                    }
                }
                continue;
            }

            try
            {
                await DeliverPendingMessagesAsync(cancellationToken).ConfigureAwait(true);

                // If there are still durable messages in the outbox (due to backpressure), wait and retry
                durableCount = Count - _pendingMessageIds.Count;
                if (durableCount > 0)
                {
                    // Configurable backoff with jitter (up to 50% additional random delay)
                    var jitterMs = (int)(_backpressureRetryDelay.TotalMilliseconds * 0.5);
                    var delay = _backpressureRetryDelay + TimeSpan.FromMilliseconds(Random.Shared.Next(0, jitterMs));
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Shutdown requested, exit gracefully
                return;
            }
            catch (Exception ex)
            {
                LogPumpLoopError(_logger, ex);

                // Wait before retrying on error
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
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
