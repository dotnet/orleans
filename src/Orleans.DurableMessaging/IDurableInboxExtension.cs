using System;
using System.Threading;
using System.Threading.Tasks;
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
    /// <param name="cancellationToken">Cancels the caller's wait for delivery.</param>
    /// <remarks>
    /// Once delivery owns inbox admission, it retains its gate and ownership reservation until
    /// its operation completes. Caller cancellation leaves that operation running to its durable outcome.
    /// The grain owner keeps delivery quiescent during journal deletion and resumes delivery
    /// after the deletion task completes successfully.
    /// </remarks>
    /// <returns>Result indicating delivery/processing status.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="envelope"/> has an empty message ID, a default sender, missing data,
    /// or identifies a receiver other than the grain handling the call.
    /// </exception>
    [Alias("DeliverAsync")]
    ValueTask<DeliveryResult> DeliverAsync(DurableEnvelope envelope, CancellationToken cancellationToken = default);
}
