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
/// </remarks>
/// <example>
/// <code>
/// public class OrderHandler : IInboxHandler&lt;OrderRequest&gt;
/// {
///     public async ValueTask HandleAsync(OrderRequest message, IInboxHandlerContext context, CancellationToken ct)
///     {
///         // Process the order
///         var result = await ProcessOrder(message);
///         
///         // Send confirmation to requester
///         if (context.Envelope.ReplyTo is { } replyTo)
///         {
///             var response = context.CreateEnvelope()
///                 .To(replyTo, "order/confirmation")
///                 .WithBody(new OrderConfirmation 
///                 { 
///                     OrderId = message.OrderId, 
///                     Status = result.Status 
///                 })
///                 .WithCorrelationKey(context.Envelope.CorrelationKey)
///                 .Build();
///             
///             context.Send(response);
///         }
///         
///         // Send notification to fulfillment service
///         var fulfillmentMessage = context.CreateEnvelope()
///             .To(fulfillmentGrain, "fulfillment/create")
///             .WithBody(new FulfillmentRequest { OrderId = message.OrderId })
///             .WithContextValue("priority", message.Priority)
///             .Build();
///         
///         context.Send(fulfillmentMessage);
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
    /// Used when setting <c>ReplyTo</c> on outbound messages to enable durable RPC patterns.
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
    /// context.Send(request);
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
    /// The builder follows a fluent API pattern:
    /// </para>
    /// <list type="number">
    /// <item><description>Call <c>.To(target, routeKey)</c> to set destination and handler route</description></item>
    /// <item><description>Call <c>.WithBody(value)</c> to serialize the message body</description></item>
    /// <item><description>Optionally call <c>.WithCorrelationKey()</c>, <c>.WithReplyTo()</c>, <c>.WithContextValue()</c></description></item>
    /// <item><description>Call <c>.Build()</c> to create the envelope</description></item>
    /// <item><description>Pass the envelope to <see cref="Send"/> to enqueue for delivery</description></item>
    /// </list>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Simple one-way message
    /// var envelope = context.CreateEnvelope()
    ///     .To(notificationGrain, "notification/send")
    ///     .WithBody(new NotificationMessage { Text = "Order complete" })
    ///     .Build();
    /// context.Send(envelope);
    /// 
    /// // Request with correlation and reply-to
    /// var request = context.CreateEnvelope()
    ///     .To(paymentGrain, "payment/authorize")
    ///     .WithBody(new PaymentRequest { Amount = 100.00m })
    ///     .WithCorrelationKey(context.Envelope.CorrelationKey?.CreateChildKey("payment"))
    ///     .WithReplyTo(context.GrainId)
    ///     .WithContextValue("idempotency-key", Guid.NewGuid().ToString())
    ///     .Build();
    /// context.Send(request);
    /// </code>
    /// </example>
    DurableEnvelopeBuilder CreateEnvelope();

    /// <summary>
    /// Sends a message via the outbox (non-generic).
    /// The envelope must be fully built via <see cref="CreateEnvelope"/>.
    /// </summary>
    /// <param name="envelope">The envelope to send.</param>
    /// <remarks>
    /// <para>
    /// The message is added to the grain's outbox and will be persisted atomically with grain state
    /// when <c>IJournaledStateManager.WriteStateAsync()</c> is called. The message will remain in the
    /// outbox until it is successfully delivered to the target grain's inbox.
    /// </para>
    /// <para>
    /// Delivery is handled by the outbox's background pump, which
    /// iterates pending outbox messages and calls <c>IDurableInboxExtension.DeliverAsync()</c> on
    /// target grains.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Send a message
    /// var envelope = context.CreateEnvelope()
    ///     .To(targetGrain, "order/process")
    ///     .WithBody(orderData)
    ///     .Build();
    /// 
    /// context.Send(envelope);
    /// // Message is now in the outbox and will be delivered asynchronously
    /// </code>
    /// </example>
    void Send(DurableEnvelope envelope);

    /// <summary>
    /// Gets the current grain's outbox for advanced scenarios.
    /// </summary>
    /// <remarks>
    /// Most handlers should use <see cref="Send"/> instead of accessing the outbox directly.
    /// Direct access is provided for advanced scenarios such as inspecting pending messages,
    /// implementing custom delivery logic, or integrating with other messaging systems.
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

    /// <summary>
    /// Sends an error response to the requester if the envelope has a ReplyTo address.
    /// </summary>
    /// <param name="errorCode">The error code categorizing the failure (e.g., "VALIDATION_FAILED").</param>
    /// <param name="message">Human-readable error message describing what went wrong.</param>
    /// <param name="isRetriable">Indicates whether the requester should retry the operation. Defaults to false.</param>
    /// <remarks>
    /// <para>
    /// This method simplifies error response handling in durable RPC scenarios. When a handler
    /// encounters an error while processing a message with a <see cref="DurableEnvelope.ReplyTo"/>
    /// address, it can call SendError to automatically create and send a <see cref="DurableErrorResponse"/>
    /// to the requester.
    /// </para>
    /// <para>
    /// The error response is sent with the same correlation key as the request, and the route key
    /// is set based on the original request's route key (e.g., "order/process" becomes "order/reply").
    /// If the request's route key doesn't contain a slash, the reply route defaults to "reply".
    /// </para>
    /// <para>
    /// If the current envelope does not have a <see cref="DurableEnvelope.ReplyTo"/> address,
    /// this method does nothing. This is a safe no-op for one-way messages that don't expect responses.
    /// </para>
    /// <para>
    /// Use standard error codes from <see cref="StandardErrorCodes"/> for common scenarios, or define
    /// custom error codes specific to your domain. Set <paramref name="isRetriable"/> to true for
    /// transient errors (network timeouts, database connection failures) and false for permanent errors
    /// (validation failures, authorization errors).
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// public class PaymentHandler : IInboxHandler&lt;PaymentRequest&gt;
    /// {
    ///     public async ValueTask HandleAsync(PaymentRequest message, IInboxHandlerContext context, CancellationToken ct)
    ///     {
    ///         // Validate request
    ///         if (message.Amount &lt;= 0)
    ///         {
    ///             context.SendError(
    ///                 StandardErrorCodes.ValidationFailed,
    ///                 "Payment amount must be greater than zero",
    ///                 isRetriable: false);
    ///             return;  // Stop processing
    ///         }
    ///         
    ///         try
    ///         {
    ///             await ProcessPayment(message, ct);
    ///         }
    ///         catch (PaymentGatewayException ex)
    ///         {
    ///             // Transient error - payment gateway unavailable
    ///             context.SendError(
    ///                 StandardErrorCodes.TransientError,
    ///                 $"Payment gateway temporarily unavailable: {ex.Message}",
    ///                 isRetriable: true);
    ///             throw;  // Rethrow to mark processing as failed
    ///         }
    ///         catch (InsufficientFundsException ex)
    ///         {
    ///             // Permanent error - payment declined
    ///             context.SendError(
    ///                 "INSUFFICIENT_FUNDS",
    ///                 $"Payment declined: {ex.Message}",
    ///                 isRetriable: false);
    ///             throw;
    ///         }
    ///     }
    /// }
    /// </code>
    /// </example>
    void SendError(string errorCode, string message, bool isRetriable = false);

    /// <summary>
    /// Sends an error response to the requester based on an exception, if the envelope has a ReplyTo address.
    /// </summary>
    /// <param name="exception">The exception that occurred during message processing.</param>
    /// <param name="isRetriable">Indicates whether the requester should retry the operation. Defaults to false.</param>
    /// <remarks>
    /// <para>
    /// This method is a convenience overload that extracts error details from an exception
    /// and sends a <see cref="DurableErrorResponse"/> to the requester. The exception type name
    /// is used as the error code (e.g., "ArgumentNullException" becomes "ARGUMENT_NULL_EXCEPTION"),
    /// and the exception message is used as the error message.
    /// </para>
    /// <para>
    /// The full exception details (including stack trace) are included in the
    /// <see cref="DurableErrorResponse.ExceptionDetails"/> property via <c>exception.ToString()</c>.
    /// In production environments, consider using the <see cref="SendError(string, string, bool)"/>
    /// overload with custom error codes and messages to avoid exposing internal implementation details.
    /// </para>
    /// <para>
    /// If the current envelope does not have a <see cref="DurableEnvelope.ReplyTo"/> address,
    /// this method does nothing. This is a safe no-op for one-way messages that don't expect responses.
    /// </para>
    /// <para>
    /// The <paramref name="isRetriable"/> parameter should be set based on the exception type:
    /// </para>
    /// <list type="bullet">
    /// <item><description><c>true</c> for transient exceptions (IOException, TimeoutException, etc.)</description></item>
    /// <item><description><c>false</c> for permanent exceptions (ArgumentException, InvalidOperationException, etc.)</description></item>
    /// </list>
    /// </remarks>
    /// <example>
    /// <code>
    /// public class OrderHandler : IInboxHandler&lt;OrderRequest&gt;
    /// {
    ///     public async ValueTask HandleAsync(OrderRequest message, IInboxHandlerContext context, CancellationToken ct)
    ///     {
    ///         try
    ///         {
    ///             await ProcessOrder(message, ct);
    ///         }
    ///         catch (ArgumentException ex)
    ///         {
    ///             // Validation error - not retriable
    ///             context.SendError(ex, isRetriable: false);
    ///             throw;
    ///         }
    ///         catch (TimeoutException ex)
    ///         {
    ///             // Transient error - retriable
    ///             context.SendError(ex, isRetriable: true);
    ///             throw;
    ///         }
    ///         catch (Exception ex)
    ///         {
    ///             // Unknown error - assume not retriable
    ///             context.SendError(ex, isRetriable: false);
    ///             throw;
    ///         }
    ///     }
    /// }
    /// </code>
    /// </example>
    void SendError(Exception exception, bool isRetriable = false);
}
