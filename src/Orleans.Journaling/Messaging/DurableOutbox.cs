using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Journaling.Messaging;

/// <summary>
/// Durable outbox implementation for sending messages.
/// Uses IDurableDictionary for persistent storage with automatic journaling.
/// </summary>
/// <remarks>
/// This basic implementation does not include delivery capability.
/// Use <see cref="DurableOutboxWithDelivery"/> for production use with synchronous delivery,
/// or integrate with <see cref="OutboxDeliveryPump"/> for asynchronous delivery via DurableJobs.
/// </remarks>
internal sealed class DurableOutbox : IDurableOutbox
{
    private readonly IDurableDictionary<Guid, DurableEnvelope> _outbox;

    /// <summary>
    /// Creates a new DurableOutbox instance.
    /// </summary>
    /// <param name="outbox">Durable dictionary for storing pending outbound messages.</param>
    public DurableOutbox(IDurableDictionary<Guid, DurableEnvelope> outbox)
    {
        ArgumentNullException.ThrowIfNull(outbox);
        _outbox = outbox;
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
    /// <remarks>
    /// The message is persisted atomically with grain state when IStateMachineManager.WriteStateAsync()
    /// is called. The message will remain in the outbox until it is successfully delivered and removed
    /// via RemoveMessage.
    /// </remarks>
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
    /// Called by the delivery pump after receiving DeliveryResult.Accepted or DeliveryResult.Duplicate
    /// from the target inbox. The removal is persisted via IStateMachineManager.WriteStateAsync().
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
    /// <remarks>
    /// Used for diagnostics, monitoring, or manual retry operations.
    /// </remarks>
    public bool TryGetMessage(Guid messageId, [MaybeNullWhen(false)] out DurableEnvelope envelope)
    {
        return _outbox.TryGetValue(messageId, out envelope);
    }

    /// <summary>
    /// This basic implementation does not support delivery.
    /// Use <see cref="DurableOutboxWithDelivery"/> for production use with delivery capability.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A completed task.</returns>
    /// <remarks>
    /// This method is a no-op in the basic implementation. Messages must be delivered
    /// by an external mechanism such as the <see cref="OutboxDeliveryPump"/>.
    /// </remarks>
    public Task DeliverPendingMessagesAsync(CancellationToken cancellationToken = default)
    {
        // No-op: basic implementation does not deliver
        // Use DurableOutboxWithDelivery or OutboxDeliveryPump for actual delivery
        return Task.CompletedTask;
    }
}
