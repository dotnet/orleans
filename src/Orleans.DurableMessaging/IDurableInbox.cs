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
    /// Removes a message after processing.
    /// </summary>
    /// <param name="senderId">The sender grain ID.</param>
    /// <param name="messageId">The message ID.</param>
    /// <returns>True if the message was removed; otherwise, false.</returns>
    bool RemoveMessage(GrainId senderId, Guid messageId);

    /// <summary>
    /// Checks if a message exists in the inbox or has been processed.
    /// </summary>
    /// <param name="senderId">The sender grain ID.</param>
    /// <param name="messageId">The message ID.</param>
    /// <returns>True if the message is in the inbox or processed dictionary; otherwise, false.</returns>
    bool ContainsOrProcessed(GrainId senderId, Guid messageId);

    /// <summary>
    /// Marks a message as processed (for deduplication tracking).
    /// </summary>
    /// <param name="senderId">The sender grain ID.</param>
    /// <param name="messageId">The message ID.</param>
    void MarkProcessed(GrainId senderId, Guid messageId);

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
    /// Tries to find a handler for the given context by calling CanHandle on registered handlers.
    /// Returns the first handler that returns true from CanHandle.
    /// </summary>
    /// <param name="context">The inbox handler context containing envelope metadata.</param>
    /// <param name="handler">The handler if found; otherwise, null.</param>
    /// <returns>True if a handler was found; otherwise, false.</returns>
    /// <remarks>
    /// <para>
    /// This method uses a cache to optimize repeated lookups for the same route key.
    /// The cache is invalidated when a new handler is registered.
    /// </para>
    /// <para>
    /// Handlers are evaluated in registration order until one returns true from CanHandle.
    /// If no handler matches, the result is cached as null to avoid repeated linear scans.
    /// </para>
    /// </remarks>
    bool TryFindHandler(IInboxHandlerContext context, [MaybeNullWhen(false)] out IInboxHandler handler);

    /// <summary>
    /// Registers a handler for a specific route (legacy method).
    /// </summary>
    /// <param name="routeKey">The route key to handle.</param>
    /// <param name="handler">The handler implementation.</param>
    /// <remarks>
    /// <para>
    /// This method is legacy and will be marked obsolete in a future release. 
    /// Use RegisterHandler(IInboxHandler) instead and implement a handler with CanHandle logic, 
    /// or use RouteKeyHandler as a base class for exact route matching.
    /// </para>
    /// <para>
    /// This method remains for backward compatibility. Handlers registered via this method
    /// are automatically wrapped in LegacyRouteKeyHandlerWrapper and added to the handler list.
    /// </para>
    /// </remarks>
    void RegisterHandler(string routeKey, IInboxHandler handler);

    /// <summary>
    /// Checks if a route has a registered handler (legacy method).
    /// </summary>
    /// <param name="routeKey">The route key to check.</param>
    /// <returns>True if a handler is registered for this route; otherwise, false.</returns>
    /// <remarks>
    /// This method is legacy and will be marked obsolete in a future release.
    /// Use TryFindHandler with an IInboxHandlerContext instead.
    /// </remarks>
    bool HasHandler(string routeKey);

    /// <summary>
    /// Tries to get a handler for a specific route (legacy method).
    /// </summary>
    /// <param name="routeKey">The route key to get the handler for.</param>
    /// <param name="handler">The handler if found.</param>
    /// <returns>True if a handler is registered for this route; otherwise, false.</returns>
    /// <remarks>
    /// This method is legacy and will be marked obsolete in a future release.
    /// Use TryFindHandler with an IInboxHandlerContext instead.
    /// </remarks>
    bool TryGetHandler(string routeKey, [MaybeNullWhen(false)] out IInboxHandler handler);
}
