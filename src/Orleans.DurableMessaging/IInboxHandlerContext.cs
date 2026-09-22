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
/// Build outbound envelopes in local variables and await <see cref="IDurableOutbox.PrepareSendAsync"/>
/// through <see cref="Outbox"/>. Call <see cref="Send"/> from the synchronous action returned by
/// <see cref="IInboxHandler.PrepareAsync"/> to stage the prepared batch alongside business changes.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public class OrderHandler : IInboxHandler&lt;OrderRequest&gt;
/// {
///     public async ValueTask&lt;Action&gt; PrepareAsync(OrderRequest message, IInboxHandlerContext context, CancellationToken ct)
///     {
///         var result = await PrepareOrderAsync(message, ct);
///
///         // Prepare both envelopes before staging either message.
///         DurableEnvelope? confirmation = null;
///         if (context.Envelope.ReplyTo is { } replyTo)
///         {
///             var confirmationBuilder = context.CreateEnvelope()
///                 .To(replyTo, "order/confirmation")
///                 .WithBody(new OrderConfirmation
///                 {
///                     OrderId = message.OrderId,
///                     Status = result.Status
///                 });
///             if (context.Envelope.CorrelationKey is { } correlationKey)
///             {
///                 confirmationBuilder.WithCorrelationKey(correlationKey);
///             }
///             confirmation = confirmationBuilder.Build();
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
///         return () =>
///         {
///             ApplyPreparedOrder(result);
///             context.Send(batch);
///         };
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
    /// return () => context.Send(batch);
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
    /// The builder and its completed envelope are local preparation values. Await
    /// <see cref="IDurableOutbox.PrepareSendAsync"/> before applying business changes and staging the batch.
    /// </para>
    /// <para>
    /// The builder follows a fluent API pattern:
    /// </para>
    /// <list type="number">
    /// <item><description>Call <c>.To(target, routeKey)</c> to set destination and handler route</description></item>
    /// <item><description>Call <c>.WithBody(value)</c> to serialize the message body</description></item>
    /// <item><description>Optionally call <c>.WithCorrelationKey()</c>, <c>.WithReplyTo()</c>, <c>.WithContextValue()</c></description></item>
    /// <item><description>Call <c>.Build()</c> to create the envelope</description></item>
    /// <item><description>Await <see cref="IDurableOutbox.PrepareSendAsync"/> through <see cref="Outbox"/> to prepare the outgoing batch</description></item>
    /// <item><description>Call <see cref="Send"/> with the batch from the returned apply action</description></item>
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
    /// return () => context.Send(batch);
    /// </code>
    /// </example>
    DurableEnvelopeBuilder CreateEnvelope();

    /// <summary>
    /// Synchronously stages an outgoing batch prepared for this handler attempt.
    /// </summary>
    /// <param name="batch">The live batch acquired through <see cref="Outbox"/> during preparation.</param>
    /// <remarks>
    /// <para>
    /// Await <see cref="IDurableOutbox.PrepareSendAsync"/> during <see cref="IInboxHandler.PrepareAsync"/>,
    /// then call this method from the matching returned action. Apply business changes and stage outgoing
    /// messages in the same synchronous block. Ordinary journal persistence captures those changes together
    /// with inbox completion, and acknowledged intents become eligible for dispatch.
    /// </para>
    /// <para>
    /// The runtime tracks prepared batches through the attempt and disposes them when it ends. Staging
    /// transfers ownership to the pending/captured/acknowledged outbox cohort. Repeating a send of the same
    /// live, already-staged batch has no additional effect within the matching attempt's current action.
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
    /// return () => context.Send(batch);
    /// </code>
    /// </example>
    /// <exception cref="System.ArgumentNullException"><paramref name="batch"/> is null.</exception>
    /// <exception cref="System.ObjectDisposedException">The batch has been disposed.</exception>
    /// <exception cref="System.InvalidOperationException">The batch belongs to another owner or attempt, its scope is invalid, or an envelope conflicts with an existing message.</exception>
    void Send(IPreparedOutboxBatch batch);

    /// <summary>
    /// Gets the handler-scoped outbox for preparing batches and inspecting pending messages.
    /// </summary>
    /// <remarks>
    /// Acquire batches with <see cref="IDurableOutbox.PrepareSendAsync"/> during handler preparation.
    /// Stage them from the matching apply action using <see cref="Send"/> or <see cref="IDurableOutbox.Send"/>.
    /// Both paths enforce the same attempt ownership. The runtime owns attempt-end disposal and delivery.
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
