using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Orleans.DurableMessaging;

/// <summary>
/// Durable inbox for receiving and processing messages.
/// </summary>
public interface IDurableInbox
{
    /// <summary>
    /// Number of unprocessed messages.
    /// </summary>
    int Count { get; }

    /// <summary>
    /// Maximum capacity. When reached, DeliverAsync returns Backpressured.
    /// </summary>
    int Capacity { get; }

    /// <summary>
    /// Gets all pending messages (no ordering guarantee).
    /// </summary>
    IEnumerable<DurableEnvelope> Messages { get; }

    /// <summary>
    /// Tries to get a specific message by its key.
    /// </summary>
    /// <param name="senderId">The sender grain ID.</param>
    /// <param name="messageId">The message ID.</param>
    /// <param name="envelope">The envelope if found.</param>
    /// <returns>True if the message exists in the inbox; otherwise, false.</returns>
    bool TryGetMessage(GrainId senderId, Guid messageId, [MaybeNullWhen(false)] out DurableEnvelope envelope);

    /// <summary>
    /// Registers a handler that will be evaluated using its CanHandle method.
    /// Handlers are evaluated in registration order (first-match-wins).
    /// </summary>
    /// <param name="handler">The handler implementation.</param>
    /// <remarks>
    /// <para>
    /// This is the recommended registration method. Handlers are stored in a list and
    /// evaluated in registration order. The first handler whose CanHandle method returns
    /// true will process the message.
    /// </para>
    /// <para>
    /// For best performance, register more specific handlers before more general ones.
    /// For example, register RouteKeyHandler instances before RoutePrefixHandler instances.
    /// </para>
    /// </remarks>
    void RegisterHandler(IInboxHandler handler);

    /// <summary>
    /// Registers a handler for a specific route.
    /// </summary>
    /// <param name="routeKey">The route key to handle.</param>
    /// <param name="handler">The handler implementation.</param>
    /// <remarks>
    /// This overload adapts the handler to exact, ordinal route matching. Use
    /// <see cref="RegisterHandler(IInboxHandler)"/> for metadata-based matching. An exact route
    /// can only be registered once for an inbox.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// An exact route handler is already registered for <paramref name="routeKey"/>.
    /// </exception>
    void RegisterHandler(string routeKey, IInboxHandler handler);

    /// <summary>
    /// Checks if an exact route has a registered handler.
    /// </summary>
    /// <param name="routeKey">The route key to check.</param>
    /// <returns>True if a handler is registered for this route; otherwise, false.</returns>
    /// <exception cref="ArgumentException">Thrown if <paramref name="routeKey"/> is null, empty, or whitespace.</exception>
    bool HasHandler(string routeKey);

    /// <summary>
    /// Tries to get a handler registered for an exact route.
    /// </summary>
    /// <param name="routeKey">The route key to get the handler for.</param>
    /// <param name="handler">The handler if found.</param>
    /// <returns>True if a handler is registered for this route; otherwise, false.</returns>
    /// <exception cref="ArgumentException">Thrown if <paramref name="routeKey"/> is null, empty, or whitespace.</exception>
    bool TryGetHandler(string routeKey, [MaybeNullWhen(false)] out IInboxHandler handler);
}
