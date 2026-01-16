using System;
using System.Collections.Generic;
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
internal sealed class DurableInboxExtension : IDurableInboxExtension
{
    private readonly IGrainContext _grainContext;
    private readonly IStateMachineManager _stateMachineManager;
    private readonly SerializerSessionPool _sessionPool;
    private readonly ILogger<DurableInboxExtension> _logger;
    private readonly IDictionary<(GrainId SenderId, Guid MessageId), DurableEnvelope> _inbox;
    private readonly IDictionary<(GrainId SenderId, Guid MessageId), DateTimeOffset> _processed;
    private readonly Dictionary<string, IInboxHandler> _handlers;
    private readonly Dictionary<Guid, TaskCompletionSource<DeliveryResult>> _pendingDeliveries;
    private readonly int _maxCapacity;
    private readonly TimeSpan _deduplicationWindow;
    private readonly int _processingConcurrency;
    private readonly SemaphoreSlim _processingLock = new(1, 1);
    private volatile bool _processingRequested;

    /// <summary>
    /// Creates a new inbox extension instance.
    /// </summary>
    /// <param name="grainContext">The grain context for this extension.</param>
    /// <param name="stateMachineManager">State machine manager for atomic persistence.</param>
    /// <param name="sessionPool">Serializer session pool for envelope creation.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="inbox">Durable dictionary for inbox messages.</param>
    /// <param name="processed">Durable dictionary for processed message tracking.</param>
    /// <param name="maxCapacity">Maximum inbox capacity (default: 1000).</param>
    /// <param name="deduplicationWindow">How long to track processed messages (default: 7 days).</param>
    /// <param name="processingConcurrency">Maximum number of messages to process concurrently (default: 1).</param>
    public DurableInboxExtension(
        IGrainContext grainContext,
        IStateMachineManager stateMachineManager,
        SerializerSessionPool sessionPool,
        ILogger<DurableInboxExtension> logger,
        IDictionary<(GrainId SenderId, Guid MessageId), DurableEnvelope> inbox,
        IDictionary<(GrainId SenderId, Guid MessageId), DateTimeOffset> processed,
        int maxCapacity = 1000,
        TimeSpan? deduplicationWindow = null,
        int processingConcurrency = 1)
    {
        ArgumentNullException.ThrowIfNull(grainContext);
        ArgumentNullException.ThrowIfNull(stateMachineManager);
        ArgumentNullException.ThrowIfNull(sessionPool);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(inbox);
        ArgumentNullException.ThrowIfNull(processed);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processingConcurrency);

        _grainContext = grainContext;
        _stateMachineManager = stateMachineManager;
        _sessionPool = sessionPool;
        _logger = logger;
        _inbox = inbox;
        _processed = processed;
        _handlers = new Dictionary<string, IInboxHandler>();
        _pendingDeliveries = new Dictionary<Guid, TaskCompletionSource<DeliveryResult>>();
        _maxCapacity = maxCapacity;
        _deduplicationWindow = deduplicationWindow ?? TimeSpan.FromDays(7);
        _processingConcurrency = processingConcurrency;
    }

    /// <summary>
    /// Gets the number of messages currently in the inbox.
    /// </summary>
    public int Count => _inbox.Count;

    /// <summary>
    /// Gets the inbox capacity limit.
    /// </summary>
    public int Capacity => _maxCapacity;

    /// <summary>
    /// Registers a handler for a specific route key.
    /// </summary>
    /// <param name="routeKey">The route key to handle.</param>
    /// <param name="handler">The handler implementation.</param>
    public void RegisterHandler(string routeKey, IInboxHandler handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeKey);
        ArgumentNullException.ThrowIfNull(handler);

        _handlers[routeKey] = handler;
        _logger.LogDebug("Registered handler for route '{RouteKey}' on grain {GrainId}", routeKey, _grainContext.GrainId);
    }

    /// <summary>
    /// Checks if a handler is registered for the specified route key.
    /// </summary>
    /// <param name="routeKey">The route key to check.</param>
    /// <returns>True if a handler is registered; otherwise, false.</returns>
    public bool HasHandler(string routeKey) => _handlers.ContainsKey(routeKey);

    /// <summary>
    /// Tries to get a handler for the specified route key.
    /// </summary>
    /// <param name="routeKey">The route key to get the handler for.</param>
    /// <param name="handler">The handler if found.</param>
    /// <returns>True if a handler is registered; otherwise, false.</returns>
    public bool TryGetHandler(string routeKey, [MaybeNullWhen(false)] out IInboxHandler handler) => _handlers.TryGetValue(routeKey, out handler);

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
        var key = (envelope.SenderId, envelope.MessageId);

        // Check for duplicate (already processed)
        if (_processed.ContainsKey(key))
        {
            _logger.LogDebug(
                "Duplicate message {MessageId} from {SenderId} to {ReceiverId} on route '{RouteKey}'",
                envelope.MessageId,
                envelope.SenderId,
                envelope.ReceiverId,
                envelope.RouteKey);

            return DeliveryResult.Duplicate();
        }

        // Check for duplicate (already in inbox)
        if (_inbox.ContainsKey(key))
        {
            _logger.LogDebug(
                "Duplicate message {MessageId} from {SenderId} already in inbox for {ReceiverId} on route '{RouteKey}'",
                envelope.MessageId,
                envelope.SenderId,
                envelope.ReceiverId,
                envelope.RouteKey);

            // If long-polling, wait for existing processing to complete
            if (options.PollTimeout > TimeSpan.Zero)
            {
                return await WaitForProcessingAsync(envelope.MessageId, options.PollTimeout, cancellationToken).ConfigureAwait(false);
            }

            return DeliveryResult.Duplicate();
        }

        // Check capacity (backpressure)
        if (_inbox.Count >= _maxCapacity)
        {
            _logger.LogWarning(
                "Inbox at capacity ({Count}/{Capacity}) for grain {GrainId}, rejecting message {MessageId} from {SenderId}",
                _inbox.Count,
                _maxCapacity,
                _grainContext.GrainId,
                envelope.MessageId,
                envelope.SenderId);

            return DeliveryResult.Backpressured();
        }

        // Check if handler exists
        if (!_handlers.ContainsKey(envelope.RouteKey))
        {
            _logger.LogWarning(
                "No handler registered for route '{RouteKey}' on grain {GrainId}, rejecting message {MessageId} from {SenderId}",
                envelope.RouteKey,
                _grainContext.GrainId,
                envelope.MessageId,
                envelope.SenderId);

            return DeliveryResult.RouteNotFound(envelope.RouteKey);
        }

        // Accept message into inbox
        _inbox[key] = envelope;

        // Persist atomically
        await _stateMachineManager.WriteStateAsync(CancellationToken.None).ConfigureAwait(false);

        _logger.LogInformation(
            "Accepted message {MessageId} from {SenderId} to {ReceiverId} on route '{RouteKey}' (CorrelationKey: {CorrelationKey})",
            envelope.MessageId,
            envelope.SenderId,
            envelope.ReceiverId,
            envelope.RouteKey,
            envelope.CorrelationKey?.ToString() ?? "(none)");

        // If long-polling, wait for processing to complete
        if (options.PollTimeout > TimeSpan.Zero)
        {
            return await WaitForProcessingAsync(envelope.MessageId, options.PollTimeout, cancellationToken).ConfigureAwait(false);
        }

        // Trigger async processing (fire-and-forget)
        _ = ProcessPendingMessagesAsync();

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
            // Trigger async processing
            _ = ProcessPendingMessagesAsync();

            // Wait for completion or timeout
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);

            var task = await Task.WhenAny(tcs.Task, Task.Delay(timeout, cts.Token)).ConfigureAwait(false);

            if (task == tcs.Task)
            {
                return await tcs.Task.ConfigureAwait(false);
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
    /// Processes all pending messages in the inbox with configured concurrency.
    /// Uses a lock to prevent concurrent processing runs, and loops until all messages are processed.
    /// </summary>
    private async Task ProcessPendingMessagesAsync()
    {
        // Mark that processing was requested
        _processingRequested = true;

        // Try to acquire the processing lock
        if (!await _processingLock.WaitAsync(0).ConfigureAwait(false))
        {
            return; // Another processing run is already in progress, it will pick up new messages
        }

        try
        {
            // Keep processing until no more messages or no more processing requests
            while (_inbox.Count > 0)
            {
                // Clear the processing request flag before processing
                _processingRequested = false;

                // Create a snapshot of pending messages to avoid collection modification
                var pending = new List<DurableEnvelope>(_inbox.Values);

                // Use ParallelOptions to control concurrency
                var parallelOptions = new ParallelOptions
                {
                    MaxDegreeOfParallelism = _processingConcurrency
                };

                await Parallel.ForEachAsync(pending, parallelOptions, async (envelope, cancellationToken) =>
                {
                    try
                    {
                        await ProcessMessageAsync(envelope).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(
                            ex,
                            "Error processing message {MessageId} from {SenderId} on route '{RouteKey}'",
                            envelope.MessageId,
                            envelope.SenderId,
                            envelope.RouteKey);
                    }
                }).ConfigureAwait(false);

                // If no new processing requests came in, we're done
                if (!_processingRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            _processingLock.Release();
        }
    }

    /// <summary>
    /// Processes a single message by invoking its handler.
    /// </summary>
    private async Task ProcessMessageAsync(DurableEnvelope envelope)
    {
        var key = (envelope.SenderId, envelope.MessageId);

        // Check if already processed (concurrent processing guard)
        if (!_inbox.ContainsKey(key))
        {
            return;
        }

        // Get handler
        if (!_handlers.TryGetValue(envelope.RouteKey, out var handler))
        {
            _logger.LogWarning(
                "Handler for route '{RouteKey}' not found during processing of message {MessageId}",
                envelope.RouteKey,
                envelope.MessageId);

            // Remove from inbox and mark as processed
            _inbox.Remove(key);
            _processed[key] = DateTimeOffset.UtcNow;
            await _stateMachineManager.WriteStateAsync(CancellationToken.None).ConfigureAwait(false);

            // Notify waiters
            CompleteDelivery(envelope.MessageId, DeliveryResult.RouteNotFound(envelope.RouteKey));
            return;
        }

        try
        {
            // Create handler context
            // Note: We need an outbox implementation - for now, create a stub
            var outbox = new InMemoryOutbox();
            var context = new InboxHandlerContext(envelope, _grainContext.GrainId, outbox, _sessionPool);

            // Invoke handler
            await handler.HandleAsync(envelope, context, CancellationToken.None).ConfigureAwait(false);

            // Mark as processed
            _inbox.Remove(key);
            _processed[key] = DateTimeOffset.UtcNow;

            // Persist atomically
            await _stateMachineManager.WriteStateAsync(CancellationToken.None).ConfigureAwait(false);

            _logger.LogInformation(
                "Processed message {MessageId} from {SenderId} on route '{RouteKey}'",
                envelope.MessageId,
                envelope.SenderId,
                envelope.RouteKey);

            // Check for response in outbox
            var response = outbox.Messages.FirstOrDefault();

            // Notify waiters
            CompleteDelivery(envelope.MessageId, DeliveryResult.Processed(response));
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Handler threw exception for message {MessageId} from {SenderId} on route '{RouteKey}'",
                envelope.MessageId,
                envelope.SenderId,
                envelope.RouteKey);

            // For now, mark as processed to avoid infinite retry
            // In production, this should use a retry policy or dead-letter queue
            _inbox.Remove(key);
            _processed[key] = DateTimeOffset.UtcNow;
            await _stateMachineManager.WriteStateAsync(CancellationToken.None).ConfigureAwait(false);

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

    /// <summary>
    /// Stub outbox implementation for handler context.
    /// TODO: Replace with actual DurableOutbox implementation.
    /// </summary>
    private sealed class InMemoryOutbox : IDurableOutbox
    {
        private readonly List<DurableEnvelope> _messages = new();

        public int Count => _messages.Count;

        public IEnumerable<DurableEnvelope> Messages => _messages;

        public void Send(DurableEnvelope envelope) => _messages.Add(envelope);

        public bool RemoveMessage(Guid messageId)
        {
            var index = _messages.FindIndex(e => e.MessageId == messageId);
            if (index >= 0)
            {
                _messages.RemoveAt(index);
                return true;
            }
            return false;
        }

        public bool TryGetMessage(Guid messageId, out DurableEnvelope envelope)
        {
            envelope = _messages.FirstOrDefault(e => e.MessageId == messageId);
            return envelope.MessageId != Guid.Empty;
        }
    }
}
