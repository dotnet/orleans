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
    private readonly IDurableInbox _durableInbox;
    private readonly IDictionary<(GrainId SenderId, Guid MessageId), DurableEnvelope> _inboxDict;
    private readonly IDictionary<(GrainId SenderId, Guid MessageId), DateTimeOffset> _processed;
    private readonly IDurableOutbox _outbox;
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
    /// <param name="durableInbox">The grain's durable inbox (shared with grain DI).</param>
    /// <param name="inboxDict">Durable dictionary for inbox messages.</param>
    /// <param name="processed">Durable dictionary for processed message tracking.</param>
    /// <param name="outbox">Durable outbox for sending response messages.</param>
    /// <param name="maxCapacity">Maximum inbox capacity (default: 1000).</param>
    /// <param name="deduplicationWindow">How long to track processed messages (default: 7 days).</param>
    /// <param name="processingConcurrency">Maximum number of messages to process concurrently (default: 1).</param>
    public DurableInboxExtension(
        IGrainContext grainContext,
        IStateMachineManager stateMachineManager,
        SerializerSessionPool sessionPool,
        ILogger<DurableInboxExtension> logger,
        IDurableInbox durableInbox,
        IDictionary<(GrainId SenderId, Guid MessageId), DurableEnvelope> inboxDict,
        IDictionary<(GrainId SenderId, Guid MessageId), DateTimeOffset> processed,
        IDurableOutbox outbox,
        int maxCapacity = 1000,
        TimeSpan? deduplicationWindow = null,
        int processingConcurrency = 1)
    {
        ArgumentNullException.ThrowIfNull(grainContext);
        ArgumentNullException.ThrowIfNull(stateMachineManager);
        ArgumentNullException.ThrowIfNull(sessionPool);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(durableInbox);
        ArgumentNullException.ThrowIfNull(inboxDict);
        ArgumentNullException.ThrowIfNull(processed);
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processingConcurrency);

        _grainContext = grainContext;
        _stateMachineManager = stateMachineManager;
        _sessionPool = sessionPool;
        _logger = logger;
        _durableInbox = durableInbox;
        _inboxDict = inboxDict;
        _processed = processed;
        _outbox = outbox;
        _pendingDeliveries = new Dictionary<Guid, TaskCompletionSource<DeliveryResult>>();
        _maxCapacity = maxCapacity;
        _deduplicationWindow = deduplicationWindow ?? TimeSpan.FromDays(7);
        _processingConcurrency = processingConcurrency;
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
        _logger.LogDebug("Registered handler for route '{RouteKey}' on grain {GrainId}", routeKey, _grainContext.GrainId);
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
        Console.WriteLine($"[DEBUG-INBOX] DeliverAsync: Received message {envelope.MessageId} from {envelope.SenderId} to {envelope.ReceiverId} on route '{envelope.RouteKey}'");
        
        var key = (envelope.SenderId, envelope.MessageId);

        // Check for duplicate (already processed)
        if (_processed.ContainsKey(key))
        {
            Console.WriteLine($"[DEBUG-INBOX] Message {envelope.MessageId} is duplicate (already processed)");
            _logger.LogDebug(
                "Duplicate message {MessageId} from {SenderId} to {ReceiverId} on route '{RouteKey}'",
                envelope.MessageId,
                envelope.SenderId,
                envelope.ReceiverId,
                envelope.RouteKey);

            return DeliveryResult.Duplicate();
        }

        // Check for duplicate (already in inbox)
        if (_inboxDict.ContainsKey(key))
        {
            Console.WriteLine($"[DEBUG-INBOX] Message {envelope.MessageId} is duplicate (already in inbox)");
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
        if (_inboxDict.Count >= _maxCapacity)
        {
            Console.WriteLine($"[DEBUG-INBOX] Message {envelope.MessageId} rejected - inbox at capacity ({_inboxDict.Count}/{_maxCapacity})");
            _logger.LogWarning(
                "Inbox at capacity ({Count}/{Capacity}) for grain {GrainId}, rejecting message {MessageId} from {SenderId}",
                _inboxDict.Count,
                _maxCapacity,
                _grainContext.GrainId,
                envelope.MessageId,
                envelope.SenderId);

            return DeliveryResult.Backpressured();
        }

        // Check if handler exists (use the shared durable inbox to check handlers)
        if (!_durableInbox.HasHandler(envelope.RouteKey))
        {
            // If no route handler, check if the grain implements IDurableInboxObserver
            // and the message has a CorrelationKey (for observer-based response handling)
            if (envelope.CorrelationKey is not null && _grainContext.GrainInstance is IDurableInboxObserver)
            {
                Console.WriteLine($"[DEBUG-INBOX] Message {envelope.MessageId} accepted - grain implements IDurableInboxObserver with CorrelationKey");
                // Fall through to accept the message for observer-based processing
            }
            else
            {
                Console.WriteLine($"[DEBUG-INBOX] Message {envelope.MessageId} rejected - no handler for route '{envelope.RouteKey}'");
                _logger.LogWarning(
                    "No handler registered for route '{RouteKey}' on grain {GrainId}, rejecting message {MessageId} from {SenderId}",
                    envelope.RouteKey,
                    _grainContext.GrainId,
                    envelope.MessageId,
                    envelope.SenderId);

                return DeliveryResult.RouteNotFound(envelope.RouteKey);
            }
        }

        Console.WriteLine($"[DEBUG-INBOX] Message {envelope.MessageId} accepted - handler/observer found for route '{envelope.RouteKey}'");
        
        // Accept message into inbox
        _inboxDict[key] = envelope;

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
        Console.WriteLine($"[DEBUG-INBOX] ProcessPendingMessagesAsync: Starting, inbox count={_inboxDict.Count}");
        
        // Mark that processing was requested
        _processingRequested = true;

        // Try to acquire the processing lock
        if (!await _processingLock.WaitAsync(0).ConfigureAwait(false))
        {
            Console.WriteLine($"[DEBUG-INBOX] ProcessPendingMessagesAsync: Could not acquire lock, returning");
            return; // Another processing run is already in progress, it will pick up new messages
        }

        try
        {
            // Keep processing until no more messages or no more processing requests
            while (_inboxDict.Count > 0)
            {
                Console.WriteLine($"[DEBUG-INBOX] ProcessPendingMessagesAsync: Processing loop, inbox count={_inboxDict.Count}");
                
                // Clear the processing request flag before processing
                _processingRequested = false;

                // Create a snapshot of pending messages to avoid collection modification
                var pending = new List<DurableEnvelope>(_inboxDict.Values);

                // Use ParallelOptions to control concurrency
                var parallelOptions = new ParallelOptions
                {
                    MaxDegreeOfParallelism = _processingConcurrency
                };

                await Parallel.ForEachAsync(pending, parallelOptions, async (envelope, cancellationToken) =>
                {
                    try
                    {
                        Console.WriteLine($"[DEBUG-INBOX] ProcessPendingMessagesAsync: Processing message {envelope.MessageId}");
                        await ProcessMessageAsync(envelope).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[DEBUG-INBOX] ProcessPendingMessagesAsync: Error processing message {envelope.MessageId}: {ex.Message}");
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
            
            Console.WriteLine($"[DEBUG-INBOX] ProcessPendingMessagesAsync: Done, inbox count={_inboxDict.Count}");
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
        Console.WriteLine($"[DEBUG-INBOX] ProcessMessageAsync: Processing message {envelope.MessageId} on route '{envelope.RouteKey}'");
        
        var key = (envelope.SenderId, envelope.MessageId);

        // Check if already processed (concurrent processing guard)
        if (!_inboxDict.ContainsKey(key))
        {
            Console.WriteLine($"[DEBUG-INBOX] ProcessMessageAsync: Message {envelope.MessageId} already processed (not in inbox)");
            return;
        }

        // Get handler
        if (!_durableInbox.TryGetHandler(envelope.RouteKey, out var handler))
        {
            // Check if the grain implements IDurableInboxObserver for correlation-based responses
            if (envelope.CorrelationKey is not null && _grainContext.GrainInstance is IDurableInboxObserver observer)
            {
                Console.WriteLine($"[DEBUG-INBOX] ProcessMessageAsync: No route handler, but grain implements IDurableInboxObserver - calling OnResponseAsync");
                try
                {
                    var options = new DeliveryOptions { PollTimeout = TimeSpan.Zero };
                    var result = await observer.OnResponseAsync(envelope.CorrelationKey, envelope, options, CancellationToken.None).ConfigureAwait(false);
                    
                    Console.WriteLine($"[DEBUG-INBOX] ProcessMessageAsync: Observer.OnResponseAsync returned {result.Status}");

                    // Mark as processed
                    _inboxDict.Remove(key);
                    _processed[key] = DateTimeOffset.UtcNow;
                    await _stateMachineManager.WriteStateAsync(CancellationToken.None).ConfigureAwait(false);

                    // Dispose envelope data to release ArcBuffer resources (after persistence)
                    envelope.Data.Dispose();

                    // Trigger delivery of any responses the observer queued in the outbox
                    if (_outbox.Count > 0)
                    {
                        Console.WriteLine($"[DEBUG-INBOX] ProcessMessageAsync: Delivering {_outbox.Count} response messages from outbox");
                        await _outbox.DeliverPendingMessagesAsync(CancellationToken.None).ConfigureAwait(false);
                    }

                    _logger.LogInformation(
                        "Processed message {MessageId} from {SenderId} via IDurableInboxObserver (CorrelationKey: {CorrelationKey})",
                        envelope.MessageId,
                        envelope.SenderId,
                        envelope.CorrelationKey);

                    // Notify waiters
                    CompleteDelivery(envelope.MessageId, result);
                    return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DEBUG-INBOX] ProcessMessageAsync: Observer.OnResponseAsync threw exception: {ex.GetType().Name}: {ex.Message}");
                    _logger.LogError(
                        ex,
                        "IDurableInboxObserver.OnResponseAsync threw exception for message {MessageId} from {SenderId}",
                        envelope.MessageId,
                        envelope.SenderId);

                    // Mark as processed to avoid infinite retry
                    _inboxDict.Remove(key);
                    _processed[key] = DateTimeOffset.UtcNow;
                    await _stateMachineManager.WriteStateAsync(CancellationToken.None).ConfigureAwait(false);

                    // Dispose envelope data
                    envelope.Data.Dispose();

                    // Notify waiters with error
                    CompleteDelivery(envelope.MessageId, DeliveryResult.Processed());
                    return;
                }
            }

            Console.WriteLine($"[DEBUG-INBOX] ProcessMessageAsync: No handler found for route '{envelope.RouteKey}'");
            _logger.LogWarning(
                "Handler for route '{RouteKey}' not found during processing of message {MessageId}",
                envelope.RouteKey,
                envelope.MessageId);

            // Remove from inbox and mark as processed
            _inboxDict.Remove(key);
            _processed[key] = DateTimeOffset.UtcNow;
            await _stateMachineManager.WriteStateAsync(CancellationToken.None).ConfigureAwait(false);

            // Dispose envelope data to release ArcBuffer resources
            envelope.Data.Dispose();

            // Notify waiters
            CompleteDelivery(envelope.MessageId, DeliveryResult.RouteNotFound(envelope.RouteKey));
            return;
        }

        try
        {
            Console.WriteLine($"[DEBUG-INBOX] ProcessMessageAsync: Handler found, invoking for message {envelope.MessageId}");
            
            // Create handler context using the grain's durable outbox
            var context = new InboxHandlerContext(envelope, _grainContext.GrainId, _outbox, _sessionPool);

            // Invoke handler
            await handler.HandleAsync(envelope, context, CancellationToken.None).ConfigureAwait(false);
            
            Console.WriteLine($"[DEBUG-INBOX] ProcessMessageAsync: Handler completed for message {envelope.MessageId}, outbox count={_outbox.Count}");

            // Mark as processed
            _inboxDict.Remove(key);
            _processed[key] = DateTimeOffset.UtcNow;

            // Persist atomically
            await _stateMachineManager.WriteStateAsync(CancellationToken.None).ConfigureAwait(false);

            // Dispose envelope data to release ArcBuffer resources (after persistence)
            envelope.Data.Dispose();

            // Trigger delivery of any responses the handler queued in the outbox
            if (_outbox.Count > 0)
            {
                Console.WriteLine($"[DEBUG-INBOX] ProcessMessageAsync: Delivering {_outbox.Count} response messages from outbox");
                await _outbox.DeliverPendingMessagesAsync(CancellationToken.None).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "Processed message {MessageId} from {SenderId} on route '{RouteKey}'",
                envelope.MessageId,
                envelope.SenderId,
                envelope.RouteKey);

            // Notify waiters
            CompleteDelivery(envelope.MessageId, DeliveryResult.Processed());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DEBUG-INBOX] ProcessMessageAsync: Handler threw exception for message {envelope.MessageId}: {ex.GetType().Name}: {ex.Message}");
            _logger.LogError(
                ex,
                "Handler threw exception for message {MessageId} from {SenderId} on route '{RouteKey}'",
                envelope.MessageId,
                envelope.SenderId,
                envelope.RouteKey);

            // For now, mark as processed to avoid infinite retry
            // In production, this should use a retry policy or dead-letter queue
            _inboxDict.Remove(key);
            _processed[key] = DateTimeOffset.UtcNow;
            await _stateMachineManager.WriteStateAsync(CancellationToken.None).ConfigureAwait(false);

            // Dispose envelope data to release ArcBuffer resources (after persistence)
            envelope.Data.Dispose();

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
}
