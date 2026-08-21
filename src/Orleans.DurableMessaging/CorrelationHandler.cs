using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.DurableMessaging;

/// <summary>
/// Base class for handlers that match messages based on correlation key hierarchy.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CorrelationHandler"/> simplifies implementing handlers that respond to messages
/// with a <see cref="DurableEnvelope.CorrelationKey"/> that matches or is a descendant of
/// a specific correlation key. This enables hierarchical workflow routing where a parent
/// workflow can handle messages from all child workflows.
/// </para>
/// <para>
/// The handler matches when the envelope's correlation key:
/// <list type="bullet">
/// <item><description>Exactly matches the configured correlation key, or</description></item>
/// <item><description>Is a descendant (child, grandchild, etc.) of the configured correlation key</description></item>
/// </list>
/// </para>
/// <para>
/// For example, if the correlation key is "workflow/order-123", this handler will match messages with:
/// <list type="bullet">
/// <item><description>"workflow/order-123" (exact match)</description></item>
/// <item><description>"workflow/order-123/payment" (child)</description></item>
/// <item><description>"workflow/order-123/payment/verify" (grandchild)</description></item>
/// </list>
/// </para>
/// <para>
/// For exact route key matching, use <see cref="RouteKeyHandler"/>. For prefix-based routing,
/// use <see cref="RoutePrefixHandler"/>.
/// </para>
/// <para>
/// <b>Handler Precedence:</b> When registering multiple handlers, more specific handlers
/// (like <see cref="RouteKeyHandler"/>) should be registered before generic handlers
/// to ensure correct dispatch order. First-match-wins semantics apply.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public class OrderWorkflowHandler : CorrelationHandler
/// {
///     private readonly string _orderId;
///
///     public OrderWorkflowHandler(string orderId)
///         : base(HierarchicalKey.Create($"workflow/order-{orderId}"))
///     {
///         _orderId = orderId;
///     }
///
///     protected override async ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken ct)
///     {
///         // This handler receives messages from:
///         // - The main order workflow ("workflow/order-123")
///         // - Child workflows like payment ("workflow/order-123/payment")
///         // - Grandchild workflows like verification ("workflow/order-123/payment/verify")
///
///         // Deserialize the message
///         if (!context.Envelope.Data.TryGetBody&lt;WorkflowEvent&gt;(out var workflowEvent))
///         {
///             throw new InvalidOperationException("Failed to deserialize WorkflowEvent");
///         }
///
///         // Process based on correlation hierarchy
///         if (context.Envelope.CorrelationKey == CorrelationKey)
///         {
///             // Main workflow message
///             await HandleMainWorkflow(workflowEvent, ct);
///         }
///         else
///         {
///             // Child workflow message - use correlation key to identify which child
///             await HandleChildWorkflow(context.Envelope.CorrelationKey, workflowEvent, ct);
///         }
///     }
/// }
///
/// // Registration
/// var orderId = "123";
/// var handler = new OrderWorkflowHandler(orderId);
/// inbox.RegisterHandler(handler);
/// </code>
/// </example>
public abstract class CorrelationHandler : IInboxHandler
{
    private readonly HierarchicalKey _correlationKey;

    /// <summary>
    /// Initializes a new instance of the <see cref="CorrelationHandler"/> class.
    /// </summary>
    /// <param name="correlationKey">The correlation key to match. The handler will match this key and all descendants.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="correlationKey"/> is null.</exception>
    protected CorrelationHandler(HierarchicalKey correlationKey)
    {
        ArgumentNullException.ThrowIfNull(correlationKey);
        _correlationKey = correlationKey;
    }

    /// <summary>
    /// Gets the correlation key that this handler matches.
    /// </summary>
    protected HierarchicalKey CorrelationKey => _correlationKey;

    /// <summary>
    /// Determines whether this handler can handle a message based on correlation key hierarchy.
    /// </summary>
    /// <param name="context">The handler context containing the envelope.</param>
    /// <returns>
    /// <c>true</c> if the envelope's correlation key matches or is a descendant of this handler's correlation key;
    /// otherwise, <c>false</c>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This implementation checks if the envelope's correlation key equals the configured correlation key
    /// or if the configured correlation key is an ancestor of the envelope's correlation key using
    /// <see cref="HierarchicalKey.IsAncestorOf"/>.
    /// </para>
    /// <para>
    /// Returns <c>false</c> if the envelope has no correlation key (null).
    /// </para>
    /// <para>
    /// <b>Note:</b> <see cref="HierarchicalKey.IsAncestorOf"/> returns <c>true</c> for exact matches
    /// (a key is considered an ancestor of itself), so this handler will match both the configured
    /// correlation key and all its descendants.
    /// </para>
    /// </remarks>
    public bool CanHandle(IInboxHandlerContext context)
    {
        return _correlationKey.IsAncestorOf(context.Envelope.CorrelationKey);
    }

    /// <summary>
    /// Handles a message that matches the configured correlation key or is a descendant.
    /// </summary>
    /// <param name="context">Handler context containing the envelope and methods for sending messages.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="ValueTask"/> representing the asynchronous operation.</returns>
    /// <remarks>
    /// <para>
    /// This method is only called when <see cref="CanHandle"/> returns <c>true</c>, meaning the
    /// envelope's correlation key matches or is a descendant of the configured correlation key.
    /// </para>
    /// <para>
    /// Derived classes can use <see cref="CorrelationKey"/> to compare against
    /// <c>context.Envelope.CorrelationKey</c> to determine if this is an exact match or a child workflow.
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
