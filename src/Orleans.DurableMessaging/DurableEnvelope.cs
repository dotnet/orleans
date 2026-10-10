using System;
using Orleans.Runtime;
using Orleans.Serialization.Buffers;

namespace Orleans.DurableMessaging;

/// <summary>
/// Carries immutable, opaque bytes between durable grain inboxes and outboxes.
/// </summary>
/// <remarks>
/// The application-supplied message identifier is the deduplication key within the receiving inbox.
/// Applications define their payload format, dispatch, and reply routing. Message identities are exact ordinal keys,
/// scoped to the receiving inbox across immediate senders and subjects. Admission permits up to 1,024 UTF-8
/// bytes and 32 segments per canonical identity, and 256 UTF-8 bytes per nonempty subject.
/// Each owning envelope must be disposed. Copies of the struct borrow the same ownership; use Retain to acquire an independent lifetime.
/// </remarks>
[Alias("Orleans.DurableMessaging.DurableEnvelope")]
public readonly struct DurableEnvelope : IDisposable
{
    /// <summary>
    /// Gets the application-defined command identity, preserved across retries and resubmissions.
    /// </summary>
    [Id(0)]
    public required HierarchicalKey MessageId { get; init; }

    /// <summary>
    /// Gets the nondefault sending grain identity, which records the immediate sender for provenance.
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

    /// <summary>Gets the exact ordinal application protocol subject.</summary>
    /// <remarks>Applications select decoding and handling using this nonempty subject and keep it stable for a command identity.</remarks>
    [Id(4)]
    public required string Subject { get; init; }

    /// <summary>Acquires an independently owned payload slice.</summary>
    /// <returns>An envelope which must be disposed by its owner.</returns>
    public DurableEnvelope Retain() => this with { Payload = Payload.Slice(0) };

    /// <summary>Releases this envelope's owned payload slice.</summary>
    public void Dispose() => Payload.Dispose();
}
