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
/// Derived classes override <see cref="PrepareAsync(IInboxHandlerContext, CancellationToken)"/>
/// to implement asynchronous preparation and return the synchronous apply action.
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
/// protected override ValueTask&lt;Action&gt; PrepareAsync(
///     IInboxHandlerContext context, CancellationToken ct)
/// {
///     return GetRouteSuffix(context.Envelope.RouteKey) switch
///     {
///         "create" =&gt; PrepareCreateAsync(context, ct),
///         "archive" =&gt; PrepareArchiveAsync(context, ct),
///         var operation =&gt; throw new InvalidOperationException($"Unknown operation: {operation}")
///     };
/// }
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
    /// This helper method is useful when implementing <see cref="PrepareAsync"/> to
    /// determine the specific operation within the prefix namespace.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// protected override ValueTask&lt;Action&gt; PrepareAsync(
    ///     IInboxHandlerContext context, CancellationToken ct)
    /// {
    ///     return GetRouteSuffix(context.Envelope.RouteKey) switch
    ///     {
    ///         "create" =&gt; PrepareCreateAsync(context, ct),
    ///         "archive" =&gt; PrepareArchiveAsync(context, ct),
    ///         var operation =&gt; throw new InvalidOperationException($"Unknown operation: {operation}")
    ///     };
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
    /// Prepares a message that matches the configured route key prefix.
    /// </summary>
    /// <param name="context">Handler context containing the envelope and methods for sending messages.</param>
    /// <param name="cancellationToken">The cancellation token for preparation.</param>
    /// <returns>A task whose result is a non-null synchronous action applying the prepared effects.</returns>
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
