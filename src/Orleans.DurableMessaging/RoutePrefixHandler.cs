using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.DurableMessaging;

/// <summary>
/// Base class for handlers that match messages based on a route key prefix.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RoutePrefixHandler"/> simplifies implementing handlers that respond to messages
/// with a <see cref="DurableEnvelope.RouteKey"/> that starts with a specific prefix.
/// For example, a prefix of "orders/" matches "orders/create", "orders/update", and "orders/archive".
/// Derived classes override <see cref="HandleAsync(IInboxHandlerContext, CancellationToken)"/>
/// to implement the message processing logic.
/// </para>
/// <para>
/// The prefix is automatically normalized to end with a forward slash ('/') to ensure
/// proper boundary matching. For example, "orders" becomes "orders/". This prevents false matches
/// where "order" would incorrectly match "order-archive/request".
/// </para>
/// <para>
/// For exact route matching, use <see cref="RouteKeyHandler"/> instead.
/// </para>
/// <para>
/// <b>Handler Precedence:</b> When registering multiple handlers, more specific handlers
/// (like <see cref="RouteKeyHandler"/>) should be registered before generic prefix handlers
/// to ensure correct dispatch order. First-match-wins semantics apply.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public class OrderPrefixHandler : RoutePrefixHandler
/// {
///     public OrderPrefixHandler() : base("orders/")
///     {
///     }
///
///     protected override async ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken ct)
///     {
///         // Get the route suffix to determine the specific operation
///         var suffix = GetRouteSuffix(context.Envelope.RouteKey);
///
///         switch (suffix)
///         {
///             case "create":
///                 await HandleCreate(context, ct);
///                 break;
///             case "archive":
///                 await HandleArchive(context, ct);
///                 break;
///             default:
///                 throw new InvalidOperationException($"Unknown order operation: {suffix}");
///         }
///     }
///
///     private async ValueTask HandleCreate(IInboxHandlerContext context, CancellationToken ct)
///     {
///         if (!context.Envelope.Data.TryGetBody&lt;CreateOrder&gt;(out var request))
///         {
///             throw new InvalidOperationException("Failed to deserialize CreateOrder");
///         }
///
///         // Process and send reply
///         var result = await ProcessRequest(request, ct);
///
///         if (context.Envelope.ReplyTo is { } replyTo)
///         {
///             var response = context.CreateEnvelope()
///                 .To(replyTo, "orders/created")
///                 .WithBody(result)
///                 .WithCorrelationKey(context.Envelope.CorrelationKey)
///                 .Build();
///
///             context.Send(response);
///         }
///     }
///
///     private async ValueTask HandleArchive(IInboxHandlerContext context, CancellationToken ct)
///     {
///         // ...
///     }
/// }
///
/// // Registration
/// inbox.RegisterHandler(new OrderPrefixHandler());
/// </code>
/// </example>
public abstract class RoutePrefixHandler : IInboxHandler
{
    private readonly string _prefix;

    /// <summary>
    /// Initializes a new instance of the <see cref="RoutePrefixHandler"/> class.
    /// </summary>
    /// <param name="prefix">The route key prefix to match. Automatically normalized to end with '/'.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="prefix"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="prefix"/> is empty or whitespace.</exception>
    protected RoutePrefixHandler(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        // Normalize prefix to always end with '/' for proper boundary matching
        _prefix = prefix.EndsWith('/') ? prefix : prefix + '/';
    }

    /// <summary>
    /// Gets the normalized route key prefix that this handler matches (always ends with '/').
    /// </summary>
    protected string Prefix => _prefix;

    /// <summary>
    /// Determines whether this handler can handle a message based on route key prefix matching.
    /// </summary>
    /// <param name="context">The handler context containing the envelope.</param>
    /// <returns>
    /// <c>true</c> if the envelope's route key starts with this handler's prefix;
    /// otherwise, <c>false</c>.
    /// </returns>
    /// <remarks>
    /// This implementation performs a case-sensitive prefix comparison using
    /// <see cref="string.StartsWith(string, StringComparison)"/> with
    /// <see cref="StringComparison.Ordinal"/>. Returns <c>false</c> if the route key is null.
    /// </remarks>
    public bool CanHandle(IInboxHandlerContext context)
    {
        return context.Envelope.RouteKey?.StartsWith(_prefix, StringComparison.Ordinal) == true;
    }

    /// <summary>
    /// Gets the suffix of a route key after removing this handler's prefix.
    /// </summary>
    /// <param name="routeKey">The full route key from the envelope.</param>
    /// <returns>
    /// The route key suffix after removing the prefix, or <c>null</c> if the route key
    /// does not start with the prefix or is null.
    /// </returns>
    /// <remarks>
    /// <para>
    /// For example, if the prefix is "orders/" and the route key is "orders/create",
    /// this method returns "create".
    /// </para>
    /// <para>
    /// This helper method is useful when implementing <see cref="HandleAsync"/> to
    /// determine the specific operation within the prefix namespace.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// protected override async ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken ct)
    /// {
    ///     var operation = GetRouteSuffix(context.Envelope.RouteKey);
    ///
    ///     switch (operation)
    ///     {
    ///         case "create":
    ///             await HandleCreate(context, ct);
    ///             break;
    ///         case "archive":
    ///             await HandleArchive(context, ct);
    ///             break;
    ///         default:
    ///             throw new InvalidOperationException($"Unknown operation: {operation}");
    ///     }
    /// }
    /// </code>
    /// </example>
    protected string? GetRouteSuffix(string? routeKey)
    {
        if (string.IsNullOrEmpty(routeKey))
        {
            return null;
        }

        if (routeKey.StartsWith(_prefix, StringComparison.Ordinal))
        {
            return routeKey.Substring(_prefix.Length);
        }

        return null;
    }

    /// <summary>
    /// Handles a message that matches the configured route key prefix.
    /// </summary>
    /// <param name="context">Handler context containing the envelope and methods for sending messages.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="ValueTask"/> representing the asynchronous operation.</returns>
    /// <remarks>
    /// <para>
    /// This method is only called when <see cref="CanHandle"/> returns <c>true</c>, meaning the
    /// envelope's route key starts with the configured prefix.
    /// </para>
    /// <para>
    /// Derived classes can use <see cref="GetRouteSuffix"/> to extract the portion of the
    /// route key after the prefix to determine the specific operation to perform.
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
