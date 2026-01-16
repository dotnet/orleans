using System;

namespace Orleans.Journaling.Messaging;

/// <summary>
/// Envelope for durable inbox/outbox messages.
/// Body and RequestContext are stored as opaque ArcBuffer slices for deferred deserialization.
/// </summary>
/// <remarks>
/// <para>
/// The DurableEnvelope provides a non-generic, polymorphic wrapper for durable messages between grains.
/// It uses deferred deserialization via <see cref="DurableEnvelopeData"/> to prevent serialization errors
/// from crashing grains during recovery.
/// </para>
/// <para>
/// Messages are uniquely identified by the composite key (SenderId, MessageId) for deduplication tracking.
/// The RouteKey field enables multiplexing multiple message types to different handlers within a single grain,
/// following Orleans' established patterns for streaming (subscriptionId) and transactions (resourceId).
/// </para>
/// <para>
/// For durable RPC (request/response) scenarios, use the CorrelationKey field to establish hierarchical
/// relationships between correlated messages (e.g., "transfer-123/debit", "transfer-123/credit").
/// The ReplyTo field specifies the grain to which responses should be sent.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Creating an envelope using DurableEnvelopeBuilder:
/// var envelope = context.CreateEnvelope()
///     .To(targetGrain, "payment/process")
///     .WithBody(new PaymentRequest { Amount = 100.00m })
///     .WithCorrelationKey("order-12345/payment")
///     .WithReplyTo(context.GrainId)
///     .Build();
/// 
/// context.Send(envelope);
/// </code>
/// </example>
[GenerateSerializer, Immutable]
public readonly struct DurableEnvelope
{
    /// <summary>
    /// Unique identifier for this message instance, used for deduplication.
    /// </summary>
    /// <remarks>
    /// Combined with <see cref="SenderId"/>, this forms the composite deduplication key
    /// (SenderId, MessageId) that prevents duplicate message processing. The MessageId
    /// is typically generated as a new GUID when the message is created.
    /// </remarks>
    [Id(0)]
    public required Guid MessageId { get; init; }

    /// <summary>
    /// Identity of the sending grain.
    /// </summary>
    /// <remarks>
    /// Used in combination with <see cref="MessageId"/> for deduplication tracking.
    /// When processing a message, the inbox checks if (SenderId, MessageId) has already
    /// been processed to ensure exactly-once semantics.
    /// </remarks>
    [Id(1)]
    public required GrainId SenderId { get; init; }

    /// <summary>
    /// Identity of the target grain.
    /// </summary>
    /// <remarks>
    /// Specifies the destination grain for this message. The inbox extension on the
    /// receiver grain will validate that a handler is registered for the specified
    /// <see cref="RouteKey"/> before accepting the message.
    /// </remarks>
    [Id(2)]
    public required GrainId ReceiverId { get; init; }

    /// <summary>
    /// Routing key for handler dispatch. Analogous to subscriptionId/resourceId in other extensions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The RouteKey enables multiplexing multiple message types to different handlers within
    /// a single grain's inbox, following established Orleans patterns:
    /// </para>
    /// <list type="bullet">
    /// <item><description>Streaming uses subscriptionId to route stream items to subscription handlers</description></item>
    /// <item><description>Transactions use resourceId to route operations to transactional resources</description></item>
    /// <item><description>Inbox/Outbox uses RouteKey to route messages to registered handlers</description></item>
    /// </list>
    /// <para>
    /// Handlers are registered using <c>IDurableInbox.RegisterHandler(string routeKey, IInboxHandler handler)</c>.
    /// If no handler is registered for the specified RouteKey, delivery returns <c>DeliveryResult.RouteNotFound()</c>.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Register handlers for different message types
    /// inbox.RegisterHandler("payment/process", new PaymentHandler());
    /// inbox.RegisterHandler("order/confirm", new OrderConfirmationHandler());
    /// inbox.RegisterHandler("refund/initiate", new RefundHandler());
    /// 
    /// // Messages are routed based on RouteKey
    /// var envelope = builder.To(targetGrain, "payment/process").WithBody(request).Build();
    /// </code>
    /// </example>
    [Id(3)]
    public required string RouteKey { get; init; }

    /// <summary>
    /// Optional hierarchical correlation key for request/response pairing.
    /// Supports parent/child relationships for correlated sub-requests.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The CorrelationKey provides a hierarchical, human-readable identifier for tracking
    /// related messages across distributed request/response flows. Unlike a GUID, hierarchical
    /// keys support parent/child relationships and are easy to trace in logs and distributed traces.
    /// </para>
    /// <para>
    /// This pattern is modeled after <c>HierarchicalKey</c> (TaskId) from <c>System.Distributed.DurableTasks</c>,
    /// enabling consistent correlation across Orleans durable messaging and workflow systems.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Simple correlation
    /// var envelope = builder
    ///     .WithCorrelationKey("order-12345")
    ///     .Build();
    /// 
    /// // Hierarchical correlation for orchestrated operations
    /// var transferKey = CorrelationKey.Create("transfer-abc");
    /// 
    /// // Child operations inherit correlation hierarchy
    /// var debitEnvelope = builder
    ///     .WithCorrelationKey(transferKey.CreateChildKey("debit"))  // "transfer-abc/debit"
    ///     .Build();
    /// 
    /// var creditEnvelope = builder
    ///     .WithCorrelationKey(transferKey.CreateChildKey("credit")) // "transfer-abc/credit"
    ///     .Build();
    /// 
    /// // Handlers can check relationships
    /// if (envelope.CorrelationKey?.IsChildOf(transferKey) == true)
    /// {
    ///     // This message is part of the transfer-abc operation
    /// }
    /// </code>
    /// </example>
    [Id(4)]
    public CorrelationKey? CorrelationKey { get; init; }

    /// <summary>
    /// Optional reply-to grain ID for durable RPC callbacks.
    /// A reference can be created from this GrainId as needed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For durable request/response patterns, the ReplyTo field specifies the grain that should
    /// receive the response. Unlike observer references (which have lifecycle and serialization issues),
    /// storing the GrainId provides a stable, durable reference that can be used to create a grain
    /// reference when needed.
    /// </para>
    /// <para>
    /// The reply message should use the same <see cref="CorrelationKey"/> to enable matching
    /// requests with their responses.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Sender creates request with ReplyTo
    /// var request = builder
    ///     .To(targetGrain, "payment/process")
    ///     .WithBody(new PaymentRequest { Amount = 100.00m })
    ///     .WithCorrelationKey("order-12345")
    ///     .WithReplyTo(context.GrainId)  // Specify where to send response
    ///     .Build();
    /// 
    /// context.Send(request);
    /// 
    /// // Handler sends reply
    /// public async ValueTask HandleAsync(PaymentRequest request, IInboxHandlerContext context, CancellationToken ct)
    /// {
    ///     var result = await ProcessPayment(request);
    ///     
    ///     if (context.Envelope.ReplyTo is { } replyTo)
    ///     {
    ///         var response = context.CreateEnvelope()
    ///             .To(replyTo, "payment/response")
    ///             .WithBody(result)
    ///             .WithCorrelationKey(context.Envelope.CorrelationKey)  // Preserve correlation
    ///             .Build();
    ///         
    ///         context.Send(response);
    ///     }
    /// }
    /// </code>
    /// </example>
    [Id(5)]
    public GrainId? ReplyTo { get; init; }

    /// <summary>
    /// Opaque data containing the serialized body and request context.
    /// Uses deferred deserialization to prevent serialization errors from crashing grains.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The Data field stores the message body and request context as opaque byte slices in a shared
    /// <c>ArcBuffer</c>, using the MigrationContext pattern from Orleans.Core. This provides several
    /// critical benefits:
    /// </para>
    /// <list type="bullet">
    /// <item><description><b>Deferred deserialization:</b> Body and context values are only deserialized when accessed</description></item>
    /// <item><description><b>Error isolation:</b> Deserialization failures don't crash grains; they return false from Try* methods</description></item>
    /// <item><description><b>Recovery safety:</b> Grains can recover even if message types are no longer available</description></item>
    /// <item><description><b>Zero-copy slicing:</b> All data shares the same underlying buffer, eliminating copying</description></item>
    /// <item><description><b>Per-key context access:</b> Individual context values can be retrieved independently</description></item>
    /// </list>
    /// <para>
    /// Access the body using <c>Data.TryGetBody&lt;T&gt;()</c> and context values using
    /// <c>Data.TryGetContextValue&lt;T&gt;(key)</c>. For forwarding messages without deserialization,
    /// use <c>Data.GetBodyBytes()</c> or <c>Data.TryGetContextBytes(key)</c>.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Accessing the message body with type safety
    /// if (envelope.Data.TryGetBody&lt;PaymentRequest&gt;(out var request))
    /// {
    ///     // Successfully deserialized as PaymentRequest
    ///     await ProcessPayment(request);
    /// }
    /// else
    /// {
    ///     // Type mismatch, corruption, or missing type
    ///     // Grain doesn't crash - can log, skip, or dead-letter
    ///     _logger.LogWarning("Failed to deserialize message body");
    /// }
    /// 
    /// // Accessing specific context values
    /// if (envelope.Data.TryGetContextValue&lt;string&gt;("TraceId", out var traceId))
    /// {
    ///     // Use trace ID for distributed tracing
    /// }
    /// 
    /// // Forwarding without deserialization (zero-copy)
    /// var bodyBytes = envelope.Data.GetBodyBytes();
    /// ForwardToAnotherSystem(bodyBytes);
    /// </code>
    /// </example>
    [Id(6)]
    public required DurableEnvelopeData Data { get; init; }

    /// <summary>
    /// Timestamp when the message was created.
    /// </summary>
    /// <remarks>
    /// Used for diagnostics, monitoring, and potential message expiration policies.
    /// The timestamp is typically set to <c>DateTimeOffset.UtcNow</c> when the envelope is built.
    /// </remarks>
    [Id(7)]
    public DateTimeOffset CreatedAt { get; init; }
}
