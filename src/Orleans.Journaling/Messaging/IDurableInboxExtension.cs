namespace Orleans.Journaling.Messaging;

/// <summary>
/// Non-generic grain extension for durable inbox message delivery.
/// Supports long-polling via DeliveryOptions, similar to DurableTasks' SubscribeOrPollAsync.
/// </summary>
[Alias("IDurableInboxExtension")]
public interface IDurableInboxExtension : IGrainExtension
{
    /// <summary>
    /// Delivers a message to this grain's durable inbox.
    /// Supports long-polling: if PollTimeout > 0, waits for processing before returning.
    /// </summary>
    /// <param name="envelope">The message envelope.</param>
    /// <param name="options">Delivery options including poll timeout.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result indicating delivery/processing status.</returns>
    [Alias("DeliverAsync"), AlwaysInterleave]
    ValueTask<DeliveryResult> DeliverAsync(DurableEnvelope envelope, DeliveryOptions options, CancellationToken cancellationToken = default);
}

/// <summary>
/// Observer interface for durable RPC reply callbacks.
/// Returns DeliveryResult to allow backpressure on reply delivery.
/// </summary>
[Alias("IDurableInboxObserver")]
public interface IDurableInboxObserver : IGrainExtension
{
    /// <summary>
    /// Called when a response is available for a correlated request.
    /// Returns DeliveryResult to allow backpressure signaling.
    /// </summary>
    /// <param name="correlationId">The correlation ID matching the original request.</param>
    /// <param name="response">The response envelope.</param>
    /// <param name="options">Delivery options (supports long-polling for chained responses).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [Alias("OnResponse"), AlwaysInterleave]
    ValueTask<DeliveryResult> OnResponseAsync(Guid correlationId, DurableEnvelope response, DeliveryOptions options, CancellationToken cancellationToken = default);
}
