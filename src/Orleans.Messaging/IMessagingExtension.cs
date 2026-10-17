using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Serialization;

namespace Orleans.Messaging;

/// <summary>
/// The grain extension through which other grains exchange durable messages with this grain.
/// </summary>
[Alias("IMessagingExtension")]
public interface IMessagingExtension : IGrainExtension
{
    /// <summary>
    /// Delivers a message to this grain's inbox.
    /// </summary>
    /// <param name="message">The received command with a required identity and payload; its receiver is this grain.</param>
    /// <param name="cancellationToken">Cancels the caller's wait for delivery.</param>
    /// <remarks>
    /// Direct admission shares the payload array, header dictionary, and header value arrays under the immutable-publication contract.
    /// RPC copying and deserialization isolate payload arrays, header dictionaries, and header value arrays using ordinary Orleans serialization.
    /// A successful result means the inbox durably accepted the message, or already processed it.
    /// </remarks>
    /// <returns>Result indicating delivery/processing status.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="message"/> has an unset message ID, exceeds identity admission limits, or has invalid payload or headers.
    /// Optional standard metadata is validated where its contract is consumed.
    /// </exception>
    [Alias("DeliverAsync")]
    ValueTask<DeliveryResult> DeliverAsync(InboxMessage message, CancellationToken cancellationToken = default);
}
