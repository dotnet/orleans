using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Journaling;
using Orleans.Runtime;

namespace Orleans.DurableMessaging;

/// <summary>
/// Durable inbox implementation for receiving and processing messages.
/// Uses IDurableDictionary for persistent storage with deduplication support.
/// </summary>
internal sealed class DurableInbox : IDurableInbox
{
    private readonly IDurableDictionary<(GrainId SenderId, Guid MessageId), DurableEnvelope> _inbox;
    private readonly List<IInboxHandler> _handlers;
    private readonly Dictionary<string, IInboxHandler> _exactRouteHandlers;
    private readonly int _capacity;

    /// <summary>
    /// Creates a new DurableInbox instance.
    /// </summary>
    /// <param name="inbox">Durable dictionary for storing unprocessed messages.</param>
    /// <param name="capacity">Maximum inbox capacity (default: 1000).</param>
    public DurableInbox(
        IDurableDictionary<(GrainId SenderId, Guid MessageId), DurableEnvelope> inbox,
        int capacity = 1000)
    {
        ArgumentNullException.ThrowIfNull(inbox);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        _inbox = inbox;
        _handlers = new List<IInboxHandler>();
        _exactRouteHandlers = new Dictionary<string, IInboxHandler>(StringComparer.Ordinal);
        _capacity = capacity;
    }

    internal DurableInbox(
        IDurableDictionary<(GrainId SenderId, Guid MessageId), DurableEnvelope> inbox,
        IEnumerable<IInboxHandler> handlers,
        int capacity)
        : this(inbox, capacity)
    {
        foreach (var handler in handlers)
        {
            RegisterHandler(handler);
        }
    }

    /// <summary>
    /// Number of unprocessed messages.
    /// </summary>
    public int Count => _inbox.Count;

    /// <summary>
    /// Maximum capacity. When reached, DeliverAsync returns Backpressured.
    /// </summary>
    public int Capacity => _capacity;

    /// <summary>
    /// Gets all pending messages (no ordering guarantee).
    /// </summary>
    public IEnumerable<DurableEnvelope> Messages => _inbox.Values;

    /// <summary>
    /// Tries to get a specific message by its key.
    /// </summary>
    /// <param name="senderId">The sender grain ID.</param>
    /// <param name="messageId">The message ID.</param>
    /// <param name="envelope">The envelope if found.</param>
    /// <returns>True if the message exists in the inbox; otherwise, false.</returns>
    public bool TryGetMessage(GrainId senderId, Guid messageId, [MaybeNullWhen(false)] out DurableEnvelope envelope)
    {
        var key = (senderId, messageId);
        return _inbox.TryGetValue(key, out envelope);
    }

    /// <summary>
    /// Registers a handler that will be evaluated using its CanHandle method.
    /// When no exact route is registered, handlers are evaluated in registration order (first-match-wins).
    /// </summary>
    /// <param name="handler">The handler implementation.</param>
    public void RegisterHandler(IInboxHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        _handlers.Add(handler);

    }

    /// <summary>
    /// Tries to find a handler for the given context.
    /// Exact route registrations take precedence; otherwise, returns the first generic handler
    /// that returns true from CanHandle.
    /// </summary>
    /// <param name="context">The inbox handler context containing envelope metadata.</param>
    /// <param name="handler">The handler if found; otherwise, null.</param>
    /// <returns>True if a handler was found; otherwise, false.</returns>
    internal bool TryFindHandler(IInboxHandlerContext context, [MaybeNullWhen(false)] out IInboxHandler handler)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_exactRouteHandlers.TryGetValue(context.Envelope.RouteKey, out handler))
        {
            return true;
        }

        foreach (var candidate in _handlers)
        {
            if (candidate.CanHandle(context))
            {
                handler = candidate;
                return true;
            }
        }

        handler = null;
        return false;
    }

    /// <summary>
    /// Registers a handler for a specific route.
    /// </summary>
    /// <param name="routeKey">The route key to handle.</param>
    /// <param name="handler">The handler implementation.</param>
    public void RegisterHandler(string routeKey, IInboxHandler handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeKey);
        ArgumentNullException.ThrowIfNull(handler);

        var wrappedHandler = new ExactRouteKeyHandlerWrapper(routeKey, handler);
        if (!_exactRouteHandlers.TryAdd(routeKey, wrappedHandler))
        {
            throw new InvalidOperationException($"A handler is already registered for exact route '{routeKey}'.");
        }

    }

    /// <summary>
    /// Checks if a route has a registered handler.
    /// </summary>
    /// <param name="routeKey">The route key to check.</param>
    /// <returns>True if a handler is registered for this route; otherwise, false.</returns>
    /// <exception cref="ArgumentException">Thrown if <paramref name="routeKey"/> is null, empty, or whitespace.</exception>
    public bool HasHandler(string routeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeKey);
        return _exactRouteHandlers.ContainsKey(routeKey);
    }

    /// <summary>
    /// Tries to get a handler for a specific route.
    /// </summary>
    /// <param name="routeKey">The route key to get the handler for.</param>
    /// <param name="handler">The handler if found.</param>
    /// <returns>True if a handler is registered for this route; otherwise, false.</returns>
    /// <exception cref="ArgumentException">Thrown if <paramref name="routeKey"/> is null, empty, or whitespace.</exception>
    public bool TryGetHandler(string routeKey, [MaybeNullWhen(false)] out IInboxHandler handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeKey);
        return _exactRouteHandlers.TryGetValue(routeKey, out handler);
    }

}

/// <summary>
/// Internal wrapper that adapts exact route registration to the capability-based handler contract.
/// </summary>
internal sealed class ExactRouteKeyHandlerWrapper : IInboxHandler
{
    private readonly string _routeKey;
    private readonly IInboxHandler _innerHandler;

    public ExactRouteKeyHandlerWrapper(string routeKey, IInboxHandler innerHandler)
    {
        _routeKey = routeKey;
        _innerHandler = innerHandler;
    }

    public bool CanHandle(IInboxHandlerContext context)
    {
        return context.Envelope.RouteKey == _routeKey;
    }

    public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        return _innerHandler.HandleAsync(context, cancellationToken);
    }
}
