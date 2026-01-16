using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Journaling.Messaging;

/// <summary>
/// Handler for messages delivered to a specific route.
/// </summary>
/// <remarks>
/// <para>
/// Handlers are registered with an inbox using <c>IDurableInbox.RegisterHandler(string routeKey, IInboxHandler handler)</c>.
/// When a message arrives with a matching RouteKey, the inbox invokes the handler with the full envelope and a context
/// for sending outbound messages.
/// </para>
/// <para>
/// For strongly-typed message handling, implement <see cref="IInboxHandler{TMessage}"/> instead, which provides
/// automatic deserialization and type checking.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public class PaymentHandler : IInboxHandler&lt;PaymentRequest&gt;
/// {
///     public async ValueTask HandleAsync(PaymentRequest request, IInboxHandlerContext context, CancellationToken ct)
///     {
///         var result = await ProcessPayment(request);
///         
///         // Send reply if requested
///         if (context.Envelope.ReplyTo is { } replyTo)
///         {
///             var response = context.CreateEnvelope()
///                 .To(replyTo, "payment/response")
///                 .WithBody(result)
///                 .WithCorrelationKey(context.Envelope.CorrelationKey)
///                 .Build();
///             
///             context.Send(response);
///         }
///     }
/// }
/// 
/// // Registration
/// inbox.RegisterHandler("payment/process", new PaymentHandler());
/// </code>
/// </example>
public interface IInboxHandler
{
    /// <summary>
    /// Handles a message from the inbox.
    /// </summary>
    /// <param name="envelope">The full message envelope (exposed for metadata access).</param>
    /// <param name="context">Handler context for creating and sending envelopes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="ValueTask"/> representing the asynchronous operation.</returns>
    /// <remarks>
    /// The handler should not throw exceptions for business logic errors; instead, it should handle them
    /// gracefully (e.g., log, send error response, etc.). Unhandled exceptions will be logged and may
    /// prevent the message from being marked as processed, depending on the inbox configuration.
    /// </remarks>
    ValueTask HandleAsync(DurableEnvelope envelope, IInboxHandlerContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Typed handler adapter for strongly-typed message handling.
/// </summary>
/// <typeparam name="TMessage">The type of message this handler processes.</typeparam>
/// <remarks>
/// <para>
/// Implementing this interface provides automatic deserialization and type checking of the message body.
/// If deserialization fails (type mismatch, missing type, etc.), the handler throws an <see cref="InvalidOperationException"/>.
/// </para>
/// <para>
/// For handlers that need to handle deserialization failures gracefully, implement <see cref="IInboxHandler"/>
/// directly and use <c>envelope.Data.TryGetBody&lt;T&gt;()</c> to attempt deserialization.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [GenerateSerializer]
/// public record PaymentRequest
/// {
///     [Id(0)] public required decimal Amount { get; init; }
///     [Id(1)] public required string AccountId { get; init; }
/// }
/// 
/// public class PaymentHandler : IInboxHandler&lt;PaymentRequest&gt;
/// {
///     private readonly IPaymentService _paymentService;
///     
///     public PaymentHandler(IPaymentService paymentService)
///     {
///         _paymentService = paymentService;
///     }
///     
///     public async ValueTask HandleAsync(PaymentRequest message, IInboxHandlerContext context, CancellationToken ct)
///     {
///         // Message is already deserialized and type-checked
///         var result = await _paymentService.ProcessPayment(message.AccountId, message.Amount, ct);
///         
///         // Send reply with result
///         if (context.Envelope.ReplyTo is { } replyTo)
///         {
///             var response = context.CreateEnvelope()
///                 .To(replyTo, "payment/response")
///                 .WithBody(new PaymentResult { Success = result, TransactionId = Guid.NewGuid() })
///                 .WithCorrelationKey(context.Envelope.CorrelationKey)
///                 .Build();
///             
///             context.Send(response);
///         }
///     }
/// }
/// </code>
/// </example>
public interface IInboxHandler<TMessage> : IInboxHandler
{
    /// <summary>
    /// Handles a typed message.
    /// </summary>
    /// <param name="message">The deserialized message body.</param>
    /// <param name="context">Handler context for creating and sending envelopes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="ValueTask"/> representing the asynchronous operation.</returns>
    ValueTask HandleAsync(TMessage message, IInboxHandlerContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Default implementation with type check and deferred deserialization.
    /// </summary>
    /// <remarks>
    /// This method attempts to deserialize the envelope body as <typeparamref name="TMessage"/>.
    /// If deserialization fails, it throws an <see cref="InvalidOperationException"/>.
    /// </remarks>
    ValueTask IInboxHandler.HandleAsync(DurableEnvelope envelope, IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        if (envelope.Data.TryGetBody<TMessage>(out var typed))
        {
            return HandleAsync(typed, context, cancellationToken);
        }

        throw new InvalidOperationException($"Failed to deserialize message body as {typeof(TMessage).Name}");
    }
}
