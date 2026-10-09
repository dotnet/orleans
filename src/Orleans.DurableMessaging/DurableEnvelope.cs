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
/// Each owning envelope must be disposed. Copies of the struct borrow the same ownership; use Retain to acquire an independent lifetime.
/// </remarks>
[Alias("Orleans.DurableMessaging.DurableEnvelope")]
public readonly struct DurableEnvelope : IDisposable
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
    public required ArcBuffer Payload { get; init; }

    /// <summary>Acquires an independently owned payload slice.</summary>
    /// <returns>An envelope which must be disposed by its owner.</returns>
    public DurableEnvelope Retain() => this with { Payload = Payload.Slice(0) };

    /// <summary>Releases this envelope's owned payload slice.</summary>
    public void Dispose() => Payload.Dispose();
}
