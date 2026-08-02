using Orleans;
using Orleans.Concurrency;
using Orleans.Runtime;
using Orleans.Serialization;

namespace Orleans.DurableMessaging;

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
