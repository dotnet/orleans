using Orleans.Runtime;
using Orleans.Serialization.Session;

namespace Orleans.DurableMessaging;

/// <summary>
/// Implementation of <see cref="IInboxHandlerContext"/> that provides handler access to envelope metadata
/// and methods for creating and sending outbound messages.
/// </summary>
/// <remarks>
/// <para>
/// This class is instantiated by the inbox processing pump when invoking handlers. It wraps the current
/// envelope and outbox, and provides a factory method for creating pre-configured envelope builders.
/// </para>
/// <para>
/// The implementation is immutable and thread-safe. Envelope builders created via <see cref="CreateEnvelope"/>
/// are independent instances and can be used concurrently (though individual builders are not thread-safe).
/// </para>
/// </remarks>
internal sealed class InboxHandlerContext : IInboxHandlerContext
{
    private readonly SerializerSessionPool _sessionPool;

    /// <summary>
    /// Initializes a new instance of the <see cref="InboxHandlerContext"/> class.
    /// </summary>
    /// <param name="envelope">The envelope being processed.</param>
    /// <param name="grainId">The current grain's identity.</param>
    /// <param name="outbox">The outbox for sending messages.</param>
    /// <param name="sessionPool">The serializer session pool for creating envelope builders.</param>
    /// <remarks>
    /// This constructor is typically called by the inbox processing pump. The parameters are captured
    /// and exposed via the <see cref="IInboxHandlerContext"/> interface properties.
    /// </remarks>
    public InboxHandlerContext(
        DurableEnvelope envelope,
        GrainId grainId,
        IDurableOutbox outbox,
        SerializerSessionPool sessionPool)
    {
        Envelope = envelope;
        GrainId = grainId;
        Outbox = outbox;
        _sessionPool = sessionPool;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The envelope contains all message metadata including <c>SenderId</c>, <c>CorrelationKey</c>,
    /// <c>ReplyTo</c>, and <c>CreatedAt</c>. The <c>Data</c> property provides access to the body
    /// and request context values via deferred deserialization.
    /// </remarks>
    public DurableEnvelope Envelope { get; }

    /// <inheritdoc />
    /// <remarks>
    /// This GrainId is automatically set as the <c>SenderId</c> on all envelopes created via
    /// <see cref="CreateEnvelope"/>.
    /// </remarks>
    public GrainId GrainId { get; }

    /// <inheritdoc />
    /// <remarks>
    /// Direct access to the outbox is provided for advanced scenarios. Most handlers should use
    /// <see cref="Send"/> instead of calling <c>Outbox.Send()</c> directly.
    /// </remarks>
    public IDurableOutbox Outbox { get; }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The returned builder has its internal properties pre-configured:
    /// </para>
    /// <list type="bullet">
    /// <item><description><c>SessionPool</c> - Set to the grain's serializer session pool</description></item>
    /// <item><description><c>SenderId</c> - Set to the current grain's GrainId</description></item>
    /// </list>
    /// <para>
    /// Each call creates a new builder instance. Builders are lightweight and intended to be used
    /// for a single envelope creation, then discarded. For high-throughput scenarios, the builder
    /// supports pooling via its internal <c>Reset()</c> method, though this is typically managed
    /// by infrastructure code rather than user handlers.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Create and send a message
    /// var envelope = context.CreateEnvelope()
    ///     .To(targetGrain, "order/confirm")
    ///     .WithBody(new OrderConfirmation { OrderId = orderId })
    ///     .Build();
    ///
    /// context.Send(envelope);
    ///
    /// // Create multiple messages with the same context
    /// var notification = context.CreateEnvelope()
    ///     .To(notificationGrain, "notification/send")
    ///     .WithBody(new Notification { Message = "Order confirmed" })
    ///     .Build();
    ///
    /// var audit = context.CreateEnvelope()
    ///     .To(auditGrain, "audit/log")
    ///     .WithBody(new AuditEvent { Action = "OrderConfirmed", OrderId = orderId })
    ///     .Build();
    ///
    /// context.Send(notification);
    /// context.Send(audit);
    /// </code>
    /// </example>
    public DurableEnvelopeBuilder CreateEnvelope()
    {
        return new DurableEnvelopeBuilder
        {
            SessionPool = _sessionPool,
            SenderId = GrainId
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The envelope is added to the outbox immediately, but persistence is deferred until
    /// <c>IStateMachineManager.WriteStateAsync()</c> is called (typically after the handler completes
    /// successfully). This ensures that outbound messages are persisted atomically with any grain
    /// state changes made during handler execution.
    /// </para>
    /// <para>
    /// If the handler throws an exception before state is persisted, the message will not be sent.
    /// This provides transactional semantics: either the handler completes and all outbound messages
    /// are sent, or the handler fails and no messages are sent.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// public async ValueTask HandleAsync(OrderRequest request, IInboxHandlerContext context, CancellationToken ct)
    /// {
    ///     // Process order (may throw exceptions)
    ///     var result = await ProcessOrder(request);
    ///
    ///     // These messages are only persisted if ProcessOrder succeeds
    ///     var confirmation = context.CreateEnvelope()
    ///         .To(request.CustomerId, "order/confirmed")
    ///         .WithBody(result)
    ///         .Build();
    ///     context.Send(confirmation);
    ///
    ///     var fulfillment = context.CreateEnvelope()
    ///         .To(fulfillmentGrain, "fulfillment/create")
    ///         .WithBody(result)
    ///         .Build();
    ///     context.Send(fulfillment);
    ///
    ///     // If we reach here, both messages will be persisted atomically
    /// }
    /// </code>
    /// </example>
    public void Send(DurableEnvelope envelope)
    {
        Outbox.Send(envelope);
    }
}
