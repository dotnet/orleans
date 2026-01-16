namespace Orleans.Journaling.Messaging;

/// <summary>
/// Envelope for durable inbox/outbox messages.
/// Body and RequestContext are stored as opaque ArcBuffer slices for deferred deserialization.
/// </summary>
[GenerateSerializer, Immutable]
public readonly struct DurableEnvelope
{
    /// <summary>
    /// Unique identifier for this message instance, used for deduplication.
    /// </summary>
    [Id(0)]
    public required Guid MessageId { get; init; }

    /// <summary>
    /// Identity of the sending grain.
    /// </summary>
    [Id(1)]
    public required GrainId SenderId { get; init; }

    /// <summary>
    /// Identity of the target grain.
    /// </summary>
    [Id(2)]
    public required GrainId ReceiverId { get; init; }

    /// <summary>
    /// Routing key for handler dispatch. Analogous to subscriptionId/resourceId in other extensions.
    /// </summary>
    [Id(3)]
    public required string RouteKey { get; init; }

    /// <summary>
    /// Optional correlation ID for request/response pairing.
    /// </summary>
    [Id(4)]
    public Guid? CorrelationId { get; init; }

    /// <summary>
    /// Optional reply-to grain ID for durable RPC callbacks.
    /// A reference can be created from this GrainId as needed.
    /// </summary>
    [Id(5)]
    public GrainId? ReplyTo { get; init; }

    /// <summary>
    /// Opaque data containing the serialized body and request context.
    /// Uses deferred deserialization to prevent serialization errors from crashing grains.
    /// </summary>
    [Id(6)]
    public required DurableEnvelopeData Data { get; init; }

    /// <summary>
    /// Timestamp when the message was created.
    /// </summary>
    [Id(7)]
    public DateTimeOffset CreatedAt { get; init; }
}
