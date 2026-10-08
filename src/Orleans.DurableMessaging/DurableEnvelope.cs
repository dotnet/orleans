using System;
using Orleans.Runtime;
using Orleans.Serialization.Buffers;

namespace Orleans.DurableMessaging;

/// <summary>
/// Carries immutable, opaque bytes between durable grain inboxes and outboxes.
/// </summary>
/// <remarks>
/// The sender and message identifier form the transport deduplication key.
/// Applications define their payload format, dispatch, reply routing, and business-operation identity.
/// The immutable payload remains valid while any envelope or application reference retains it.
/// </remarks>
[GenerateSerializer, Alias("Orleans.DurableMessaging.DurableEnvelope")]
public readonly struct DurableEnvelope
{
    /// <summary>
    /// Gets the nonempty identifier of this delivery, preserved across transport retries.
    /// </summary>
    [Id(0)]
    public required Guid MessageId { get; init; }

    /// <summary>
    /// Gets the nondefault sending grain identity, paired with <see cref="MessageId"/> for deduplication.
    /// </summary>
    [Id(1)]
    public required GrainId SenderId { get; init; }

    /// <summary>
    /// Gets the destination grain identity.
    /// </summary>
    [Id(2)]
    public required GrainId ReceiverId { get; init; }

    /// <summary>
    /// Gets the immutable application bytes, decoded by the receiving handler.
    /// </summary>
    /// <remarks>
    /// Empty payloads are valid. Application decoding takes place during handler preparation,
    /// before shared mutations. Create the payload locally before staging the envelope.
    /// </remarks>
    [Id(3)]
    public required ImmutableBuffer Payload { get; init; }
}
