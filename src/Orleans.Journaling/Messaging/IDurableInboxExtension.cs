using Orleans;
using Orleans.Concurrency;
using Orleans.Runtime;
using Orleans.Serialization;

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
    /// Supports long-polling: if PollTimeout &gt; 0, waits for processing before returning.
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
    /// <param name="correlationKey">The hierarchical correlation key matching the original request.</param>
    /// <param name="response">The response envelope.</param>
    /// <param name="options">Delivery options (supports long-polling for chained responses).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result indicating delivery/processing status.</returns>
    /// <remarks>
    /// The <paramref name="correlationKey"/> parameter uses hierarchical <see cref="HierarchicalKey"/> 
    /// to support parent/child request relationships. This enables correlated sub-requests 
    /// (e.g., "transfer-123" → "transfer-123/debit" → "transfer-123/credit").
    /// </remarks>
    [Alias("OnResponse"), AlwaysInterleave]
    ValueTask<DeliveryResult> OnResponseAsync(HierarchicalKey correlationKey, DurableEnvelope response, DeliveryOptions options, CancellationToken cancellationToken = default);
}
