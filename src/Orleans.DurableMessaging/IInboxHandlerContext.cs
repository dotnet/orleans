using Orleans.Runtime;

namespace Orleans.DurableMessaging;

/// <summary>
/// Context available during inbox message handling.
/// Non-generic interface using builder pattern for envelope creation.
/// </summary>
/// <remarks>
/// <para>
/// The handler context provides access to the current envelope being processed, the grain's identity,
/// and methods for creating and sending outbound messages. It follows Orleans' established patterns
/// for non-generic extension interfaces with builder-based message creation.
/// </para>
/// <para>
/// The <see cref="CreateEnvelope"/> method returns a <see cref="DurableEnvelopeBuilder"/> pre-configured
/// with the current grain's <c>SenderId</c> and serialization session pool. This ensures that outbound
/// messages are properly attributed and serialized without requiring handlers to manage infrastructure concerns.
/// </para>
/// <para>
/// Build outbound envelopes in local variables and call <see cref="Send(DurableEnvelope)"/> from the
/// synchronous final block of <see cref="IInboxHandler.HandleAsync"/> alongside business changes.
/// Call <see cref="Complete"/> in that block and return without further awaits.
/// The journal establishes the self-wakeup before capture. Optional <see cref="IDurableOutbox.PrepareSendAsync"/>
/// establishes this prerequisite earlier and returns a batch to stage in the same final block.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public class OrderHandler : IInboxHandler&lt;OrderRequest&gt;
/// {
///     public async ValueTask HandleAsync(OrderRequest message, IInboxHandlerContext context, CancellationToken ct)
///     {
///         var result = await PrepareOrderAsync(message, ct);
///
///         // Prepare both envelopes before staging either message.
///         DurableEnvelope? confirmation = null;
///         if (context.Envelope.ReplyTo is { } replyTo)
///         {
///             confirmation = context.CreateEnvelope()
///                 .To(replyTo, "order/confirmation")
///                 .WithBody(new OrderConfirmation
///                 {
///                     OrderId = message.OrderId,
///                     Status = result.Status
///                 })
///                 .WithCorrelationKey(context.Envelope.CorrelationKey)
///                 .Build();
///         }
///
///         var fulfillmentMessage = context.CreateEnvelope()
///             .To(fulfillmentGrain, "fulfillment/create")
///             .WithBody(new FulfillmentRequest { OrderId = message.OrderId })
///             .WithContextValue("priority", message.Priority)
///             .Build();
///
///         var messages = confirmation is { } response
///             ? new[] { response, fulfillmentMessage }
///             : new[] { fulfillmentMessage };
///         var batch = await context.Outbox.PrepareSendAsync(messages, ct);
///         ct.ThrowIfCancellationRequested();
///         ApplyPreparedOrder(result);
///         context.Send(batch);
///         context.Complete();
///     }
/// }
/// </code>
/// </example>
public interface IInboxHandlerContext
{
    /// <summary>
    /// The envelope being processed.
    /// </summary>
    /// <remarks>
    /// Provides access to envelope metadata such as <c>SenderId</c>, <c>CorrelationKey</c>, <c>ReplyTo</c>,
    /// and <c>CreatedAt</c>. The envelope's <c>Data</c> property can be used to access context values or
    /// raw body bytes without deserialization.
    /// </remarks>
    /// <example>
    /// <code>
    /// // Access correlation key
    /// if (context.Envelope.CorrelationKey is { } key)
    /// {
    ///     _logger.LogInformation("Processing message with correlation key: {Key}", key);
    /// }
    ///
    /// // Access request context values
    /// if (context.Envelope.Data.TryGetContextValue&lt;string&gt;("trace-id", out var traceId))
    /// {
    ///     Activity.Current?.SetTag("trace-id", traceId);
    /// }
    ///
    /// // Check reply-to for request/response pattern
    /// if (context.Envelope.ReplyTo is { } replyTo)
    /// {
    ///     // This is a request that expects a response
    /// }
    /// </code>
    /// </example>
    DurableEnvelope Envelope { get; }

    /// <summary>
    /// Gets the current grain's grain ID.
    /// </summary>
    /// <remarks>
    /// Used when setting <c>ReplyTo</c> on outbound messages for application-defined follow-up routing.
    /// The GrainId is automatically set as the <c>SenderId</c> on envelopes created via <see cref="CreateEnvelope"/>.
    /// </remarks>
    /// <example>
    /// <code>
    /// // Request with reply-to set to current grain
    /// var request = context.CreateEnvelope()
    ///     .To(workerGrain, "work/process")
    ///     .WithBody(workItem)
    ///     .WithReplyTo(context.GrainId)  // Responses come back to this grain
    ///     .Build();
    ///
    /// var batch = await context.Outbox.PrepareSendAsync([request], ct);
    /// ct.ThrowIfCancellationRequested();
    /// context.Send(batch);
    /// context.Complete();
    /// </code>
    /// </example>
    GrainId GrainId { get; }

    /// <summary>
    /// Creates a new envelope builder for sending messages.
    /// The builder's <see cref="DurableEnvelopeBuilder.WithBody{T}"/> method handles serialization.
    /// </summary>
    /// <returns>A new envelope builder pre-configured with the current grain's SenderId and session pool.</returns>
    /// <remarks>
    /// <para>
    /// The returned builder has its <c>SenderId</c> and <c>SerializerSessionPool</c> properties already set
    /// to the appropriate values for the current grain. This ensures that outbound messages are properly
    /// attributed and serialized without requiring handlers to manage these infrastructure concerns.
    /// </para>
    /// <para>
    /// The builder and its completed envelope are local preparation values. Stage the envelope with
    /// <see cref="Send(DurableEnvelope)"/> in the final synchronous block, or optionally establish the wakeup earlier
    /// using <see cref="IDurableOutbox.PrepareSendAsync"/> before staging its batch.
    /// </para>
    /// <para>
    /// The builder follows a fluent API pattern:
    /// </para>
    /// <list type="number">
    /// <item><description>Call <c>.To(target, routeKey)</c> to set destination and handler route</description></item>
    /// <item><description>Call <c>.WithBody(value)</c> to serialize the message body</description></item>
    /// <item><description>Optionally call <c>.WithCorrelationKey()</c>, <c>.WithReplyTo()</c>, <c>.WithContextValue()</c></description></item>
    /// <item><description>Call <c>.Build()</c> to create the envelope</description></item>
    /// <item><description>Call <see cref="Send(DurableEnvelope)"/> with the envelope in the final synchronous block</description></item>
    /// <item><description>For an early wakeup prerequisite, optionally await <see cref="IDurableOutbox.PrepareSendAsync"/> and use <see cref="Send(IPreparedOutboxBatch)"/></description></item>
    /// </list>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Simple one-way message
    /// var envelope = context.CreateEnvelope()
    ///     .To(notificationGrain, "notification/send")
    ///     .WithBody(new NotificationMessage { Text = "Order complete" })
    ///     .Build();
    ///
    /// // Request with correlation and reply-to
    /// var requestBuilder = context.CreateEnvelope()
    ///     .To(paymentGrain, "payment/authorize")
    ///     .WithBody(new PaymentRequest { Amount = 100.00m })
    ///     .WithReplyTo(context.GrainId)
    ///     .WithContextValue("idempotency-key", Guid.NewGuid().ToString());
    /// if (context.Envelope.CorrelationKey is { } correlationKey)
    /// {
    ///     requestBuilder.WithCorrelationKey(correlationKey.CreateChildKey("payment"));
    /// }
    ///
    /// var request = requestBuilder.Build();
    /// var batch = await context.Outbox.PrepareSendAsync([envelope, request], ct);
    /// ct.ThrowIfCancellationRequested();
    /// context.Send(batch);
    /// context.Complete();
    /// </code>
    /// </example>
    DurableEnvelopeBuilder CreateEnvelope();

    /// <summary>
    /// Synchronously stages an outgoing batch prepared for this handler attempt.
    /// </summary>
    /// <param name="batch">The live batch acquired through <see cref="Outbox"/> during preparation.</param>
    /// <remarks>
    /// <para>
    /// Await <see cref="IDurableOutbox.PrepareSendAsync"/> during <see cref="IInboxHandler.HandleAsync"/>,
    /// then apply safe-to-commit business changes, stage outgoing messages, and call <see cref="Complete"/>
    /// in the same synchronous final block. Return without further awaits. The runtime owns the later journal
    /// write and acknowledgement, after which captured intents become eligible for dispatch.
    /// </para>
    /// <para>
    /// The runtime tracks prepared batches through the attempt and disposes them when it ends. Staging
    /// transfers ownership to the pending/captured/acknowledged outbox cohort. Repeating a send of the same
    /// live, already-staged batch has no additional effect within the matching active attempt before completion.
    /// Every call validates that scope; outside-scope, stale, wrong-attempt, disposed, or foreign handles
    /// are rejected before mutation.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var envelope = context.CreateEnvelope()
    ///     .To(targetGrain, "order/process")
    ///     .WithBody(orderData)
    ///     .Build();
    /// var batch = await context.Outbox.PrepareSendAsync([envelope], ct);
    /// ct.ThrowIfCancellationRequested();
    /// context.Send(batch);
    /// context.Complete();
    /// </code>
    /// </example>
    /// <exception cref="System.ArgumentNullException"><paramref name="batch"/> is null.</exception>
    /// <exception cref="System.ObjectDisposedException">The batch has been disposed.</exception>
    /// <exception cref="System.InvalidOperationException">The batch belongs to another owner or attempt, its scope is invalid, or an envelope conflicts with an existing message.</exception>
    void Send(IPreparedOutboxBatch batch);

    /// <summary>
    /// Stages an outgoing envelope in this attempt's synchronous final block before completion.
    /// </summary>
    /// <param name="envelope">The fully built outgoing envelope.</param>
    /// <remarks>The journal establishes the self-wakeup before capture and dispatch follows acknowledgement.</remarks>
    void Send(DurableEnvelope envelope) => Outbox.Send(envelope);

    /// <summary>
    /// Synchronously stages inbox completion and deduplication for this handler attempt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Check cancellation before the first shared mutation. Apply complete safe-to-commit business changes,
    /// stage outgoing messages, and call this method in one synchronous final block. From the first shared
    /// mutation through method completion, perform no awaits, including after this call.
    /// Complete stages logical inbox state; the runtime owns the subsequent storage write and acknowledgement.
    /// </para>
    /// <para>
    /// The runtime validates the exact active attempt. Repeated completion within the same still-active
    /// completed attempt coalesces. Retired or wrong-attempt contexts are rejected. Send and preparation
    /// are rejected after completion or retirement; envelope metadata remains available for inspection.
    /// Completion of a valid final block is preserved when attempt cancellation arrives after it has started.
    /// </para>
    /// </remarks>
    /// <exception cref="System.InvalidOperationException">The context is read-only, retired, or belongs to another attempt.</exception>
    void Complete();

    /// <summary>
    /// Gets the handler-scoped outbox for sending envelopes, preparing optional batches, and inspecting pending messages.
    /// </summary>
    /// <remarks>
    /// Stage envelopes with <see cref="Send(DurableEnvelope)"/> in the active attempt before completion.
    /// Optional batches acquired with <see cref="IDurableOutbox.PrepareSendAsync"/> during preparation are
    /// staged with <see cref="Send(IPreparedOutboxBatch)"/>. Both paths enforce attempt ownership and reject
    /// sending or further preparation after completion or retirement.
    /// The runtime owns attempt-end batch disposal and delivery.
    /// </remarks>
    /// <example>
    /// <code>
    /// // Check if there are pending messages
    /// if (context.Outbox.Count &gt; 100)
    /// {
    ///     _logger.LogWarning("High outbox backlog: {Count} messages", context.Outbox.Count);
    /// }
    ///
    /// // Inspect pending messages (advanced)
    /// foreach (var pending in context.Outbox.Messages)
    /// {
    ///     var oneHourAgo = DateTimeOffset.UtcNow.AddHours(-1);
    ///     if (pending.CreatedAt &lt; oneHourAgo)
    ///     {
    ///         _logger.LogWarning("Message {Id} has been pending for over 1 hour", pending.MessageId);
    ///     }
    /// }
    /// </code>
    /// </example>
    IDurableOutbox Outbox { get; }
}
