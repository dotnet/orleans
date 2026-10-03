using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.DurableMessaging;

/// <summary>
/// Base class for handlers that match messages based on an exact route key.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RouteKeyHandler"/> simplifies implementing handlers that only respond to messages
/// with a specific <see cref="DurableEnvelope.RouteKey"/>. Derived classes override
/// <see cref="PrepareAsync(IInboxHandlerContext, CancellationToken)"/> to implement
/// asynchronous preparation and return the synchronous apply action.
/// </para>
/// <para>
/// For prefix-based routing (e.g., "orders/" matches "orders/create" and "orders/update"), derive
/// from <see cref="RoutePrefixHandler"/>.
/// </para>
/// <para>
/// <b>Handler Precedence:</b> When registering multiple handlers, more specific handlers
/// (like RouteKeyHandler) should be registered before generic handlers (like prefix or
/// correlation handlers) to ensure correct dispatch order.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// protected override async ValueTask&lt;Action&gt; PrepareAsync(
///     IInboxHandlerContext context, CancellationToken ct)
/// {
///     if (!context.Envelope.Data.TryGetBody&lt;OrderRequest&gt;(out var request))
///     {
///         throw new InvalidOperationException("Failed to deserialize OrderRequest");
///     }
///
///     var prepared = await PrepareOrderAsync(request, ct);
///     return () =&gt; ApplyOrder(prepared);
/// }
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
    /// Prepares a message that matches the configured route key.
    /// </summary>
    /// <param name="context">Handler context containing the envelope and methods for sending messages.</param>
    /// <param name="cancellationToken">The cancellation token for preparation.</param>
    /// <returns>A task whose result is a non-null synchronous action applying the prepared effects.</returns>
    /// <remarks>
    /// <para>
    /// This method is only called when <see cref="CanHandle"/> returns <c>true</c>, meaning the
    /// envelope's route key matches the configured route key.
    /// </para>
    /// <para>
    /// Follow the preparation and synchronous application requirements of <see cref="IInboxHandler.PrepareAsync"/>.
    /// The interface implementation forwards the returned action to Messaging for invocation.
    /// </para>
    /// </remarks>
    protected abstract ValueTask<Action> PrepareAsync(IInboxHandlerContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Explicit interface implementation that delegates to the protected <see cref="PrepareAsync"/> method.
    /// </summary>
    ValueTask<Action> IInboxHandler.PrepareAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        return PrepareAsync(context, cancellationToken);
    }
}
