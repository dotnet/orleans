using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Journaling.Messaging;

/// <summary>
/// Base class for handlers that match messages based on an exact route key.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RouteKeyHandler"/> simplifies implementing handlers that only respond to messages
/// with a specific <see cref="DurableEnvelope.RouteKey"/>. Derived classes override
/// <see cref="HandleAsync(IInboxHandlerContext, CancellationToken)"/> to implement
/// the message processing logic.
/// </para>
/// <para>
/// For prefix-based routing (e.g., "rpc/" matches "rpc/request", "rpc/reply"), implement
/// <see cref="IInboxHandler"/> directly with custom <see cref="IInboxHandler.CanHandle"/>
/// logic using <c>RouteKey?.StartsWith()</c>.
/// </para>
/// <para>
/// <b>Handler Precedence:</b> When registering multiple handlers, more specific handlers
/// (like RouteKeyHandler) should be registered before generic handlers (like prefix or
/// correlation handlers) to ensure correct dispatch order.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public class OrderProcessingHandler : RouteKeyHandler
/// {
///     private readonly IOrderService _orderService;
///     
///     public OrderProcessingHandler(IOrderService orderService) 
///         : base("order/process")
///     {
///         _orderService = orderService;
///     }
///     
///     protected override async ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken ct)
///     {
///         // Deserialize the message
///         if (!context.Envelope.Data.TryGetBody&lt;OrderRequest&gt;(out var request))
///         {
///             throw new InvalidOperationException("Failed to deserialize OrderRequest");
///         }
///         
///         // Process the order
///         var result = await _orderService.ProcessOrder(request, ct);
///         
///         // Send reply if requested
///         if (context.Envelope.ReplyTo is { } replyTo)
///         {
///             var response = context.CreateEnvelope()
///                 .To(replyTo, "order/response")
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
/// inbox.RegisterHandler("order/process", new OrderProcessingHandler(orderService));
/// </code>
/// </example>
public abstract class RouteKeyHandler : IInboxHandler
{
    private readonly string _routeKey;

    /// <summary>
    /// Initializes a new instance of the <see cref="RouteKeyHandler"/> class.
    /// </summary>
    /// <param name="routeKey">The exact route key to match.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="routeKey"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="routeKey"/> is empty or whitespace.</exception>
    protected RouteKeyHandler(string routeKey)
    {
        ArgumentNullException.ThrowIfNull(routeKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(routeKey);

        _routeKey = routeKey;
    }

    /// <summary>
    /// Gets the route key that this handler matches.
    /// </summary>
    protected string RouteKey => _routeKey;

    /// <summary>
    /// Determines whether this handler can handle a message based on exact route key matching.
    /// </summary>
    /// <param name="context">The handler context containing the envelope.</param>
    /// <returns>
    /// <c>true</c> if the envelope's route key exactly matches this handler's route key;
    /// otherwise, <c>false</c>.
    /// </returns>
    /// <remarks>
    /// This implementation performs an exact string comparison (case-sensitive) between
    /// <see cref="DurableEnvelope.RouteKey"/> and the route key provided in the constructor.
    /// </remarks>
    public bool CanHandle(IInboxHandlerContext context)
    {
        return context.Envelope.RouteKey == _routeKey;
    }

    /// <summary>
    /// Handles a message that matches the configured route key.
    /// </summary>
    /// <param name="context">Handler context containing the envelope and methods for sending messages.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="ValueTask"/> representing the asynchronous operation.</returns>
    /// <remarks>
    /// <para>
    /// This method is only called when <see cref="CanHandle"/> returns <c>true</c>, meaning the
    /// envelope's route key matches the configured route key.
    /// </para>
    /// <para>
    /// Derived classes should handle business logic errors gracefully (e.g., log and send error
    /// response) rather than throwing exceptions. Unhandled exceptions will be logged and may
    /// prevent the message from being marked as processed.
    /// </para>
    /// </remarks>
    protected abstract ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Explicit interface implementation that delegates to the protected <see cref="HandleAsync"/> method.
    /// </summary>
    ValueTask IInboxHandler.HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        return HandleAsync(context, cancellationToken);
    }
}
