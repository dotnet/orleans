using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Orleans.Journaling.Messaging;

/// <summary>
/// Durable outbox implementation with synchronous delivery capability.
/// Wraps the basic DurableOutbox and adds the ability to deliver messages immediately.
/// </summary>
internal sealed class DurableOutboxWithDelivery : IDurableOutbox
{
    private readonly IDurableDictionary<Guid, DurableEnvelope> _outbox;
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<DurableOutboxWithDelivery> _logger;

    /// <summary>
    /// Creates a new DurableOutboxWithDelivery instance.
    /// </summary>
    /// <param name="outbox">Durable dictionary for storing pending outbound messages.</param>
    /// <param name="grainFactory">Grain factory for accessing target grains.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    public DurableOutboxWithDelivery(
        IDurableDictionary<Guid, DurableEnvelope> outbox,
        IGrainFactory grainFactory,
        ILogger<DurableOutboxWithDelivery> logger)
    {
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(grainFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _outbox = outbox;
        _grainFactory = grainFactory;
        _logger = logger;
    }

    /// <summary>
    /// Number of pending outbound messages.
    /// </summary>
    public int Count => _outbox.Count;

    /// <summary>
    /// Gets all pending outbound messages (no ordering guarantee).
    /// </summary>
    public IEnumerable<DurableEnvelope> Messages => _outbox.Values;

    /// <summary>
    /// Enqueues a fully-built envelope for delivery (non-generic).
    /// </summary>
    /// <param name="envelope">The envelope to send.</param>
    public void Send(DurableEnvelope envelope)
    {
        // Store envelope keyed by MessageId for O(1) lookup during removal
        _outbox[envelope.MessageId] = envelope;
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
        return _outbox.Remove(messageId);
    }

    /// <summary>
    /// Tries to get a specific outbox message.
    /// </summary>
    /// <param name="messageId">The unique identifier of the message.</param>
    /// <param name="envelope">When this method returns, contains the envelope if found; otherwise, the default value.</param>
    /// <returns>True if the message was found; otherwise, false.</returns>
    public bool TryGetMessage(Guid messageId, [MaybeNullWhen(false)] out DurableEnvelope envelope)
    {
        return _outbox.TryGetValue(messageId, out envelope);
    }

    /// <summary>
    /// Triggers delivery of all pending messages in the outbox (single attempt).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the delivery operation.</returns>
    /// <remarks>
    /// This method makes a SINGLE attempt to deliver each pending message.  Messages that fail due to backpressure
    /// remain in the outbox and will be retried on subsequent WriteStateAsync() calls or by the OutboxDeliveryPump
    /// (if configured with DurableJobs). This design avoids blocking the grain for extended periods, maintaining
    /// Orleans' non-blocking grain model.
    /// </remarks>
    public async Task DeliverPendingMessagesAsync(CancellationToken cancellationToken = default)
    {
        if (_outbox.Count == 0)
        {
            return;
        }

        Console.WriteLine($"[DEBUG-OUTBOX] DeliverPendingMessagesAsync: Delivering {_outbox.Count} pending messages from outbox");
        _logger.LogDebug("Delivering {Count} pending messages from outbox", _outbox.Count);

        // Snapshot pending messages to avoid collection modification during iteration
        var pending = _outbox.Values.ToList();
        var deliveredCount = 0;
        var backpressuredCount = 0;
        var failedCount = 0;

        foreach (var envelope in pending)
        {
            try
            {
                Console.WriteLine($"[DEBUG-OUTBOX] Delivering message {envelope.MessageId} from {envelope.SenderId} to {envelope.ReceiverId} on route '{envelope.RouteKey}'");
                
                // Get the target grain's inbox extension
                var targetGrain = _grainFactory.GetGrain<IDurableInboxExtension>(envelope.ReceiverId);

                // Deliver with no long-polling (immediate return after persistence)
                var options = new DeliveryOptions { PollTimeout = TimeSpan.Zero };

                var result = await targetGrain.DeliverAsync(envelope, options, cancellationToken).ConfigureAwait(false);
                
                Console.WriteLine($"[DEBUG-OUTBOX] Delivery result for message {envelope.MessageId}: Status={result.Status}, Message={result.Message ?? "(none)"}");

                switch (result.Status)
                {
                    case DeliveryStatus.Accepted:
                    case DeliveryStatus.Duplicate:
                    case DeliveryStatus.Processed:
                        // Success - remove from outbox
                        RemoveMessage(envelope.MessageId);
                        deliveredCount++;

                        _logger.LogDebug(
                            "Delivered message {MessageId} from {SenderId} to {ReceiverId} on route '{RouteKey}' (Status: {Status})",
                            envelope.MessageId,
                            envelope.SenderId,
                            envelope.ReceiverId,
                            envelope.RouteKey,
                            result.Status);
                        break;

                    case DeliveryStatus.RouteNotFound:
                        // No handler for route - log but keep in outbox for retry
                        // (handler might be registered later)
                        _logger.LogWarning(
                            "Route not found for message {MessageId} from {SenderId} to {ReceiverId} on route '{RouteKey}': {Message}",
                            envelope.MessageId,
                            envelope.SenderId,
                            envelope.ReceiverId,
                            envelope.RouteKey,
                            result.Message ?? "(no message)");
                        failedCount++;
                        break;

                    case DeliveryStatus.Backpressured:
                        // Leave in outbox - will be retried on next WriteStateAsync or by OutboxDeliveryPump
                        backpressuredCount++;
                        _logger.LogDebug(
                            "Backpressured delivering message {MessageId} to {ReceiverId}, will retry later",
                            envelope.MessageId,
                            envelope.ReceiverId);
                        break;

                    default:
                        _logger.LogWarning(
                            "Unexpected delivery status {Status} for message {MessageId}",
                            result.Status,
                            envelope.MessageId);
                        failedCount++;
                        break;
                }
            }
            catch (Exception ex)
            {
                failedCount++;
                _logger.LogError(
                    ex,
                    "Error delivering message {MessageId} from {SenderId} to {ReceiverId} on route '{RouteKey}'",
                    envelope.MessageId,
                    envelope.SenderId,
                    envelope.ReceiverId,
                    envelope.RouteKey);
            }
        }

        _logger.LogInformation(
            "Outbox delivery complete: {DeliveredCount} delivered, {BackpressuredCount} backpressured, {FailedCount} failed, {RemainingCount} remaining",
            deliveredCount,
            backpressuredCount,
            failedCount,
            _outbox.Count);
    }
}
