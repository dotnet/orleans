using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Orleans.DurableMessaging;

/// <summary>
/// Durable outbox for sending messages.
/// Uses dictionary storage (no ordering guarantees).
/// Non-generic interface - use <see cref="DurableEnvelopeBuilder"/> to create envelopes.
/// </summary>
/// <remarks>
/// <para>
/// The outbox stores pending outbound messages in a durable dictionary until they are successfully delivered
/// to the target grain's inbox. Messages persist atomically with grain state via <c>IJournaledStateManager.WriteStateAsync()</c>.
/// </para>
/// <para>
/// Delivery is driven by the outbox's background pump, which iterates
/// pending messages and calls <c>IDurableInboxExtension.DeliverAsync()</c> on target grains. On successful
/// delivery (<c>DeliveryResult.Accepted</c> or <c>DeliveryResult.Duplicate</c>), messages are removed from
/// the outbox.
/// </para>
/// <para>
/// The outbox does NOT guarantee ordering of messages. If ordering is required, it must be implemented at
/// a higher level (e.g., using sequence numbers or correlation keys).
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Create and send a message via outbox
/// var envelope = context.CreateEnvelope()
///     .To(targetGrain, "payment/process")
///     .WithBody(new PaymentRequest { Amount = 100.00m })
///     .WithCorrelationKey("order-12345")
///     .WithReplyTo(context.GrainId)
///     .Build();
///
/// context.Outbox.Send(envelope);
///
/// // The message is now persisted and will be delivered by the outbox pump
/// </code>
/// </example>
public interface IDurableOutbox
{
    /// <summary>
    /// Number of pending outbound messages.
    /// </summary>
    /// <remarks>
    /// Used for monitoring and backpressure signaling. A high count may indicate delivery issues
    /// or backpressure from target grains.
    /// </remarks>
    int Count { get; }

    /// <summary>
    /// Gets all pending outbound messages (no ordering guarantee).
    /// </summary>
    /// <remarks>
    /// Used by the delivery pump to iterate and deliver pending messages. The order of enumeration
    /// is undefined and may change between calls.
    /// </remarks>
    IEnumerable<DurableEnvelope> Messages { get; }

    /// <summary>
    /// Enqueues a fully-built envelope for delivery (non-generic).
    /// Use <see cref="DurableEnvelopeBuilder"/> to create the envelope.
    /// </summary>
    /// <param name="envelope">The envelope to send.</param>
    /// <remarks>
    /// <para>
    /// Outside inbox handling, the message is persisted atomically with grain state when
    /// <c>IJournaledStateManager.WriteStateAsync()</c> is called. During inbox handling, direct writes are
    /// deferred and the inbox infrastructure commits handler state, outbox messages, inbox removal, and
    /// deduplication completion together after successful handler return. The message remains in the outbox
    /// until it is successfully delivered and removed via <see cref="RemoveMessage"/>.
    /// </para>
    /// <para>
    /// To create an envelope, use <c>context.CreateEnvelope()</c> in a handler, or create a
    /// <see cref="DurableEnvelopeBuilder"/> directly with the appropriate <c>SerializerSessionPool</c>
    /// and <c>SenderId</c>.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var envelope = context.CreateEnvelope()
    ///     .To(targetGrain, "order/confirm")
    ///     .WithBody(new OrderConfirmation { OrderId = "order-123" })
    ///     .Build();
    ///
    /// outbox.Send(envelope);
    /// </code>
    /// </example>
    void Send(DurableEnvelope envelope);

    /// <summary>
    /// Removes a message after successful delivery.
    /// </summary>
    /// <param name="messageId">The unique identifier of the message to remove.</param>
    /// <returns><c>true</c> if the message was found and removed; otherwise, <c>false</c>.</returns>
    /// <remarks>
    /// Called by the delivery pump after receiving <c>DeliveryResult.Accepted</c> or <c>DeliveryResult.Duplicate</c>
    /// from the target inbox. The removal should be persisted via <c>IStateMachineManager.WriteStateAsync()</c>
    /// to ensure the message is not re-delivered after grain reactivation.
    /// </remarks>
    bool RemoveMessage(Guid messageId);

    /// <summary>
    /// Tries to get a specific outbox message.
    /// </summary>
    /// <param name="messageId">The unique identifier of the message.</param>
    /// <param name="envelope">When this method returns, contains the envelope if found; otherwise, the default value.</param>
    /// <returns><c>true</c> if the message was found; otherwise, <c>false</c>.</returns>
    /// <remarks>
    /// Used for diagnostics, monitoring, or manual retry operations. In normal operation, the delivery pump
    /// iterates messages via the <see cref="Messages"/> property.
    /// </remarks>
    bool TryGetMessage(Guid messageId, [MaybeNullWhen(false)] out DurableEnvelope envelope);

    /// <summary>
    /// Triggers delivery of all pending messages in the outbox.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the delivery operation.</returns>
    /// <remarks>
    /// This method should be called after <c>IJournaledStateManager.WriteStateAsync()</c> to deliver
    /// messages that were added via <see cref="Send"/>. If the outbox is empty, this method returns immediately.
    /// Delivery is also triggered automatically after writes and when the grain activates.
    /// </remarks>
    Task DeliverPendingMessagesAsync(CancellationToken cancellationToken = default);
}

[Alias("Orleans.DurableMessaging.IDurableOutboxCommitExtension")]
internal interface IDurableOutboxCommitExtension : IGrainExtension
{
    ValueTask<bool> TryClaimJobAsync(string jobId, CancellationToken cancellationToken);

    ValueTask<DateTimeOffset?> CompleteJobAttemptAsync(string jobId, CancellationToken cancellationToken);

    ValueTask ApplyDeliveryResultAsync(
        Guid messageId,
        DeliveryResult result,
        string? failure,
        CancellationToken cancellationToken);
}
