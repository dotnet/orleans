using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Session;

namespace Orleans.Journaling.Messaging;

/// <summary>
/// Implementation of durable inbox extension for grain message delivery.
/// Handles message persistence, deduplication, long-polling, and processing.
/// </summary>
internal sealed partial class DurableInboxExtension : IDurableInboxExtension, IDisposable
{
    private readonly IGrainContext _grainContext;
    private readonly IJournaledStateManager _stateManager;
    private readonly SerializerSessionPool _sessionPool;
    private readonly ILogger<DurableInboxExtension> _logger;
    private readonly JournalingInstruments _instruments;
    private readonly IDurableInbox _durableInbox;
    private readonly IDictionary<(GrainId SenderId, Guid MessageId), DurableEnvelope> _inboxDict;
    private readonly IDictionary<(GrainId SenderId, Guid MessageId), DateTimeOffset> _processed;
    private readonly IDurableOutbox _outbox;
    private readonly Dictionary<Guid, TaskCompletionSource<DeliveryResult>> _pendingDeliveries;
    private readonly int _maxCapacity;
    private readonly TimeSpan _deduplicationWindow;
    private readonly object _processingLock = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private Task? _processingTask;
    private int _metricsActive;
    
    /// <summary>
    /// Counter incremented when new messages are added. The processing task checks this
    /// to determine if new work arrived while it was processing.
    /// </summary>
    private int _processingVersion;

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
    /// <param name="maxCapacity">Maximum inbox capacity (default: 1000).</param>
    /// <param name="deduplicationWindow">How long to track processed messages (default: 7 days).</param>
    public DurableInboxExtension(
        IGrainContext grainContext,
        IJournaledStateManager stateManager,
        SerializerSessionPool sessionPool,
        ILogger<DurableInboxExtension> logger,
        JournalingInstruments instruments,
        IDurableInbox durableInbox,
        IDictionary<(GrainId SenderId, Guid MessageId), DurableEnvelope> inboxDict,
        IDictionary<(GrainId SenderId, Guid MessageId), DateTimeOffset> processed,
        IDurableOutbox outbox,
        int maxCapacity = 1000,
        TimeSpan? deduplicationWindow = null)
    {
        ArgumentNullException.ThrowIfNull(grainContext);
        ArgumentNullException.ThrowIfNull(stateManager);
        ArgumentNullException.ThrowIfNull(sessionPool);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(instruments);
        ArgumentNullException.ThrowIfNull(durableInbox);
        ArgumentNullException.ThrowIfNull(inboxDict);
        ArgumentNullException.ThrowIfNull(processed);
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCapacity);

        _grainContext = grainContext;
        _stateManager = stateManager;
        _sessionPool = sessionPool;
        _logger = logger;
        _instruments = instruments;
        _durableInbox = durableInbox;
        _inboxDict = inboxDict;
        _processed = processed;
        _outbox = outbox;
        _pendingDeliveries = new Dictionary<Guid, TaskCompletionSource<DeliveryResult>>();
        _maxCapacity = maxCapacity;
        _deduplicationWindow = deduplicationWindow ?? TimeSpan.FromDays(7);
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
        EnsureMetricsActive();
        var key = (envelope.SenderId, envelope.MessageId);

        // Check for duplicate (already processed)
        if (_processed.ContainsKey(key))
        {
            LogDuplicateMessageDetected(
                _logger,
                envelope.MessageId,
                envelope.SenderId,
                envelope.ReceiverId,
                envelope.RouteKey,
                envelope.CorrelationKey?.ToString());

            // Record duplicate message metric
            var grainType = _grainContext.GrainId.Type.ToString();
            _instruments.OnInboxMessageReceived(grainType, envelope.RouteKey, "duplicate");

            return DeliveryResult.Duplicate();
        }

        // Check for duplicate (already in inbox)
        if (_inboxDict.ContainsKey(key))
        {
            LogDuplicateMessageInInbox(
                _logger,
                envelope.MessageId,
                envelope.SenderId,
                envelope.ReceiverId,
                envelope.RouteKey,
                envelope.CorrelationKey?.ToString());

            // Record duplicate message metric
            var grainType = _grainContext.GrainId.Type.ToString();
            _instruments.OnInboxMessageReceived(grainType, envelope.RouteKey, "duplicate");
            ScheduleProcessing();

            // If long-polling, wait for existing processing to complete
            if (options.PollTimeout > TimeSpan.Zero)
            {
                return await WaitForProcessingAsync(envelope.MessageId, options.PollTimeout, cancellationToken).ConfigureAwait(true);
            }

            return DeliveryResult.Duplicate();
        }

        // Check capacity (backpressure)
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

            // Record backpressure metric
            var grainType = _grainContext.GrainId.Type.ToString();
            _instruments.OnInboxMessageReceived(grainType, envelope.RouteKey, "backpressured");

            return DeliveryResult.Backpressured();
        }

        // Check if handler exists using capability-based dispatch
        // Create a lightweight context for handler matching (we don't need full outbox access here)
        var handlerContext = new InboxHandlerContext(envelope, _grainContext.GrainId, _outbox, _sessionPool);
        
        if (!_durableInbox.TryFindHandler(handlerContext, out _))
        {
            // If no route handler, check if the grain implements IDurableInboxObserver
            // and the message has a CorrelationKey (for observer-based response handling)
            if (envelope.CorrelationKey is not null && _grainContext.GrainInstance is IDurableInboxObserver)
            {
                // Fall through to accept the message for observer-based processing
            }
            else
            {
                LogRouteNotFound(
                    _logger,
                    envelope.RouteKey,
                    _grainContext.GrainId,
                    envelope.MessageId,
                    envelope.SenderId,
                    envelope.CorrelationKey?.ToString());

                // Record route not found metric
                var grainType = _grainContext.GrainId.Type.ToString();
                _instruments.OnInboxMessageReceived(grainType, envelope.RouteKey, "route_not_found");

                return DeliveryResult.RouteNotFound(envelope.RouteKey);
            }
        }

        // Accept message into inbox
        _inboxDict[key] = envelope;
        _instruments.OnInboxDepthChanged(1);

        // Persist atomically
        await _stateManager.WriteStateAsync(CancellationToken.None).ConfigureAwait(true);

        LogMessageAccepted(
            _logger,
            envelope.MessageId,
            envelope.SenderId,
            envelope.ReceiverId,
            envelope.RouteKey,
            envelope.CorrelationKey?.ToString());

        // Record accepted message metric
        var grainTypeName = _grainContext.GrainId.Type.ToString();
        _instruments.OnInboxMessageReceived(grainTypeName, envelope.RouteKey, "accepted");

        // If long-polling, wait for processing to complete
        if (options.PollTimeout > TimeSpan.Zero)
        {
            return await WaitForProcessingAsync(envelope.MessageId, options.PollTimeout, cancellationToken).ConfigureAwait(true);
        }

        // Schedule async processing.
        // Increment version to signal new work, then ensure a processing task is running.
        ScheduleProcessing();

        return DeliveryResult.Accepted();
    }

    /// <summary>
    /// Waits for a message to be processed, with timeout support for long-polling.
    /// </summary>
    private async ValueTask<DeliveryResult> WaitForProcessingAsync(
        Guid messageId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<DeliveryResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Register the waiter
        _pendingDeliveries[messageId] = tcs;

        try
        {
            // Schedule async processing
            ScheduleProcessing();

            // Wait for completion or timeout
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);

            var task = await Task.WhenAny(tcs.Task, Task.Delay(timeout, cts.Token)).ConfigureAwait(true);

            if (task == tcs.Task)
            {
                return await tcs.Task.ConfigureAwait(true);
            }

            // Timeout - return pending status
            return DeliveryResult.Pending();
        }
        finally
        {
            // Clean up waiter
            _pendingDeliveries.Remove(messageId);
        }
    }

    /// <summary>
    /// Schedules processing of pending messages. Thread-safe - can be called concurrently
    /// from multiple interleaved DeliverAsync calls.
    /// </summary>
    private void ScheduleProcessing()
    {
        lock (_processingLock)
        {
            // Increment version to signal new work
            Interlocked.Increment(ref _processingVersion);

            // If no processing task is running, start one.
            // Note: We must check IsCompleted because the task might still be "running"
            // (hasn't returned yet) but has already exited the processing loop.
            // The version increment above ensures any exiting task will see new work
            // and restart if needed.
            if (_processingTask is null || _processingTask.IsCompleted)
            {
                _processingTask = ProcessingLoopAsync();
            }
        }
    }

    /// <summary>
    /// Background loop that processes pending messages. Continues until inbox is empty
    /// and no new work has been signaled via _processingVersion.
    /// </summary>
    private async Task ProcessingLoopAsync()
    {
        while (!_shutdownCts.IsCancellationRequested)
        {
            // Capture current version INSIDE the lock to avoid races with ScheduleProcessing
            int versionBeforeProcessing;
            lock (_processingLock)
            {
                versionBeforeProcessing = Volatile.Read(ref _processingVersion);
            }

            // Process all current messages
            if (_inboxDict.Count > 0)
            {
                await ProcessPendingMessagesAsync().ConfigureAwait(true);
            }

            // Check if we should exit - must hold lock to coordinate with ScheduleProcessing
            lock (_processingLock)
            {
                var currentVersion = Volatile.Read(ref _processingVersion);
                
                // If version changed, new work was added - continue processing
                if (currentVersion != versionBeforeProcessing)
                {
                    continue;
                }

                // If inbox is not empty, continue processing
                if (_inboxDict.Count > 0)
                {
                    continue;
                }

                // CRITICAL: Set _processingTask to a completed task BEFORE returning.
                // This ensures any concurrent ScheduleProcessing call will see IsCompleted=true
                // and start a new task. We're inside the lock so no race can occur.
                _processingTask = Task.CompletedTask;
                return;
            }
        }
    }

    /// <summary>
    /// Processes all pending messages in the inbox sequentially.
    /// Called from ProcessingLoopAsync on the grain's scheduler.
    /// </summary>
    private async Task ProcessPendingMessagesAsync()
    {
        if (_inboxDict.Count == 0)
        {
            return;
        }

        // Create a snapshot of pending messages to avoid collection modification
        var pending = new List<DurableEnvelope>(_inboxDict.Values);

        foreach (var envelope in pending)
        {
            if (_shutdownCts.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await ProcessMessageAsync(envelope).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                LogProcessingError(
                    _logger,
                    ex,
                    envelope.MessageId,
                    envelope.SenderId,
                    envelope.RouteKey,
                    envelope.CorrelationKey?.ToString());
            }
        }
    }

    /// <summary>
    /// Processes a single message by invoking its handler.
    /// </summary>
    private async Task ProcessMessageAsync(DurableEnvelope envelope)
    {
        var key = (envelope.SenderId, envelope.MessageId);
        var grainTypeName = _grainContext.GrainId.Type.ToString();
        var stopwatch = Stopwatch.StartNew();

        // Check if already processed (concurrent processing guard)
        if (!_inboxDict.ContainsKey(key))
        {
            return;
        }

        // Create handler context for capability-based dispatch
        var context = new InboxHandlerContext(envelope, _grainContext.GrainId, _outbox, _sessionPool);

        // Get handler using capability-based dispatch (new API)
        if (!_durableInbox.TryFindHandler(context, out var handler))
        {
            // Check if the grain implements IDurableInboxObserver for correlation-based responses
            if (envelope.CorrelationKey is not null && _grainContext.GrainInstance is IDurableInboxObserver observer)
            {
                try
                {
                    var options = new DeliveryOptions { PollTimeout = TimeSpan.Zero };
                    var result = await observer.OnResponseAsync(envelope.CorrelationKey, envelope, options, CancellationToken.None).ConfigureAwait(true);

                    // Mark as processed
                    RemoveMessage(key);
                    _processed[key] = DateTimeOffset.UtcNow;
                    await _stateManager.WriteStateAsync(CancellationToken.None).ConfigureAwait(true);

                    // Dispose envelope data to release ArcBuffer resources (after persistence)
                    envelope.Data.Dispose();

                    // Trigger delivery of any responses the observer queued in the outbox
                    if (_outbox.Count > 0)
                    {
                    await _outbox.DeliverPendingMessagesAsync(CancellationToken.None).ConfigureAwait(true);
                }

                LogMessageProcessedViaObserver(
                    _logger,
                    envelope.MessageId,
                    envelope.SenderId,
                    envelope.CorrelationKey?.ToString());

                // Record processing metrics
                    stopwatch.Stop();
                    _instruments.OnInboxMessageProcessed(grainTypeName, envelope.RouteKey, "success");
                    _instruments.OnInboxProcessingDuration(stopwatch.Elapsed, grainTypeName, envelope.RouteKey);

                    // Notify waiters
                    CompleteDelivery(envelope.MessageId, result);
                    return;
                }
            catch (Exception ex)
            {
                LogObserverProcessingError(
                    _logger,
                    ex,
                    envelope.MessageId,
                    envelope.SenderId,
                    envelope.CorrelationKey?.ToString());

                // Mark as processed to avoid infinite retry
                    RemoveMessage(key);
                    _processed[key] = DateTimeOffset.UtcNow;
                    await _stateManager.WriteStateAsync(CancellationToken.None).ConfigureAwait(true);

                    // Dispose envelope data
                    envelope.Data.Dispose();

                    // Record processing metrics (error case)
                    stopwatch.Stop();
                    _instruments.OnInboxMessageProcessed(grainTypeName, envelope.RouteKey, "error");
                    _instruments.OnInboxProcessingDuration(stopwatch.Elapsed, grainTypeName, envelope.RouteKey);

                    // Notify waiters with error
                    CompleteDelivery(envelope.MessageId, DeliveryResult.Processed());
                    return;
                }
        }

        LogHandlerNotFoundDuringProcessing(
            _logger,
            envelope.RouteKey,
            envelope.MessageId,
            envelope.CorrelationKey?.ToString());

        // Remove from inbox and mark as processed
            RemoveMessage(key);
            _processed[key] = DateTimeOffset.UtcNow;
            await _stateManager.WriteStateAsync(CancellationToken.None).ConfigureAwait(true);

            // Dispose envelope data to release ArcBuffer resources
            envelope.Data.Dispose();

            // Record metrics (route not found during processing)
            stopwatch.Stop();
            _instruments.OnInboxMessageProcessed(grainTypeName, envelope.RouteKey, "route_not_found");
            _instruments.OnInboxProcessingDuration(stopwatch.Elapsed, grainTypeName, envelope.RouteKey);

            // Notify waiters
            CompleteDelivery(envelope.MessageId, DeliveryResult.RouteNotFound(envelope.RouteKey));
            return;
        }

        try
        {
            // Invoke handler (context already created above for capability-based dispatch)
            await handler.HandleAsync(context, CancellationToken.None).ConfigureAwait(true);

            // Mark as processed
            RemoveMessage(key);
            _processed[key] = DateTimeOffset.UtcNow;

            // Persist atomically
            await _stateManager.WriteStateAsync(CancellationToken.None).ConfigureAwait(true);

            // Dispose envelope data to release ArcBuffer resources (after persistence)
            envelope.Data.Dispose();

            // Trigger delivery of any responses the handler queued in the outbox
            if (_outbox.Count > 0)
            {
            await _outbox.DeliverPendingMessagesAsync(CancellationToken.None).ConfigureAwait(true);
        }

        LogMessageProcessed(
            _logger,
            envelope.MessageId,
            envelope.SenderId,
            envelope.RouteKey,
            envelope.CorrelationKey?.ToString());

        // Record processing metrics
            stopwatch.Stop();
            _instruments.OnInboxMessageProcessed(grainTypeName, envelope.RouteKey, "success");
            _instruments.OnInboxProcessingDuration(stopwatch.Elapsed, grainTypeName, envelope.RouteKey);

            // Notify waiters
            CompleteDelivery(envelope.MessageId, DeliveryResult.Processed());
        }
        catch (Exception ex)
        {
            LogHandlerException(
                _logger,
                ex,
                envelope.MessageId,
                envelope.SenderId,
                envelope.RouteKey,
                envelope.CorrelationKey?.ToString());

            // For now, mark as processed to avoid infinite retry
            // In production, this should use a retry policy or dead-letter queue
            RemoveMessage(key);
            _processed[key] = DateTimeOffset.UtcNow;
            await _stateManager.WriteStateAsync(CancellationToken.None).ConfigureAwait(true);

            // Dispose envelope data to release ArcBuffer resources (after persistence)
            envelope.Data.Dispose();

            // Record processing metrics (error case)
            stopwatch.Stop();
            _instruments.OnInboxMessageProcessed(grainTypeName, envelope.RouteKey, "error");
            _instruments.OnInboxProcessingDuration(stopwatch.Elapsed, grainTypeName, envelope.RouteKey);

            // Notify waiters with error
            CompleteDelivery(envelope.MessageId, DeliveryResult.Processed());
        }
    }

    /// <summary>
    /// Completes delivery for pending waiters.
    /// </summary>
    private void CompleteDelivery(Guid messageId, DeliveryResult result)
    {
        if (_pendingDeliveries.TryGetValue(messageId, out var tcs))
        {
            tcs.TrySetResult(result);
        }
    }

    internal void ResumeProcessing()
    {
        EnsureMetricsActive();
        if (_inboxDict.Count > 0)
        {
            ScheduleProcessing();
        }
    }

    internal void StopProcessing()
    {
        _shutdownCts.Cancel();
        if (Interlocked.Exchange(ref _metricsActive, 0) != 0)
        {
            _instruments.OnInboxDepthChanged(-_inboxDict.Count);
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
            _instruments.OnInboxDepthChanged(_inboxDict.Count);
        }
    }

    private bool RemoveMessage((GrainId SenderId, Guid MessageId) key)
    {
        if (!_inboxDict.Remove(key))
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
        Message = "Processed message {MessageId} from {SenderId} via IDurableInboxObserver (CorrelationKey: {CorrelationKey})")]
    private static partial void LogMessageProcessedViaObserver(ILogger logger, Guid messageId, GrainId senderId, string? correlationKey);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "IDurableInboxObserver.OnResponseAsync threw exception for message {MessageId} from {SenderId} (CorrelationKey: {CorrelationKey})")]
    private static partial void LogObserverProcessingError(ILogger logger, Exception exception, Guid messageId, GrainId senderId, string? correlationKey);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Processed message {MessageId} from {SenderId} on route '{RouteKey}' (CorrelationKey: {CorrelationKey})")]
    private static partial void LogMessageProcessed(ILogger logger, Guid messageId, GrainId senderId, string routeKey, string? correlationKey);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Handler threw exception for message {MessageId} from {SenderId} on route '{RouteKey}' (CorrelationKey: {CorrelationKey})")]
    private static partial void LogHandlerException(ILogger logger, Exception exception, Guid messageId, GrainId senderId, string routeKey, string? correlationKey);
}
