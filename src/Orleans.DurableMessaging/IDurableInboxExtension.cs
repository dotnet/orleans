using Orleans;
using Orleans.Runtime;
using Orleans.Serialization;

namespace Orleans.DurableMessaging;

/// <summary>
/// Non-generic grain extension for durable inbox message delivery.
/// </summary>
[Alias("IDurableInboxExtension")]
public interface IDurableInboxExtension : IGrainExtension
{
    /// <summary>
    /// Delivers a message to this grain's durable inbox.
    /// </summary>
    /// <param name="envelope">The message envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result indicating delivery/processing status.</returns>
    [Alias("DeliverAsync")]
    ValueTask<DeliveryResult> DeliverAsync(DurableEnvelope envelope, CancellationToken cancellationToken = default);
}
