using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization.TypeSystem;

namespace Orleans.DurableMessaging;

/// <summary>
/// Durable inbox implementation for receiving and processing messages.
/// Uses IDurableDictionary for persistent storage with deduplication support.
/// </summary>
internal sealed class DurableInbox : IDurableInbox, ILifecycleObserver
{
    private readonly IDurableDictionary<(GrainId SenderId, Guid MessageId), DurableEnvelope> _inbox;
    private readonly IDurableDictionary<(GrainId SenderId, Guid MessageId), DateTimeOffset> _processed;
    private readonly List<IInboxHandler> _handlers;
    private readonly Dictionary<string, IInboxHandler> _legacyRouteHandlers;
    private readonly int _capacity;
    private readonly IServiceProvider? _serviceProvider;
    private readonly DurableMessagingInstruments? _instruments;
    private DurableInboxExtension? _extension;

    /// <summary>
    /// Creates a new DurableInbox instance.
    /// </summary>
    /// <param name="inbox">Durable dictionary for storing unprocessed messages.</param>
    /// <param name="processed">Durable dictionary for tracking processed messages.</param>
    /// <param name="capacity">Maximum inbox capacity (default: 1000).</param>
    public DurableInbox(
        IDurableDictionary<(GrainId SenderId, Guid MessageId), DurableEnvelope> inbox,
        IDurableDictionary<(GrainId SenderId, Guid MessageId), DateTimeOffset> processed,
        int capacity = 1000)
    {
        ArgumentNullException.ThrowIfNull(inbox);
        ArgumentNullException.ThrowIfNull(processed);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        _inbox = inbox;
        _processed = processed;
        _handlers = new List<IInboxHandler>();
        _legacyRouteHandlers = new Dictionary<string, IInboxHandler>();
        _capacity = capacity;
    }

    internal DurableInbox(
        IDurableDictionary<(GrainId SenderId, Guid MessageId), DurableEnvelope> inbox,
        IDurableDictionary<(GrainId SenderId, Guid MessageId), DateTimeOffset> processed,
        IServiceProvider serviceProvider,
        IGrainContext grainContext,
        DurableMessagingInstruments instruments,
        IEnumerable<IInboxHandler> handlers,
        int capacity)
        : this(inbox, processed, capacity)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(grainContext);
        ArgumentNullException.ThrowIfNull(instruments);

        _serviceProvider = serviceProvider;
        _instruments = instruments;
        foreach (var handler in handlers)
        {
            RegisterHandler(handler);
        }

        grainContext.ObservableLifecycle.Subscribe(
            RuntimeTypeNameFormatter.Format(GetType()),
            GrainLifecycleStage.Last,
            this);
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
    /// Removes a message after processing and disposes its envelope data.
    /// </summary>
    /// <param name="senderId">The sender grain ID.</param>
    /// <param name="messageId">The message ID.</param>
    /// <returns>True if the message was removed; otherwise, false.</returns>
    public bool RemoveMessage(GrainId senderId, Guid messageId)
    {
        var key = (senderId, messageId);

        if (_inbox.ContainsKey(key))
        {
            var removed = _inbox.Remove(key);
            if (removed)
            {
                _instruments?.OnInboxDepthChanged(-1);
            }
            return removed;
        }

        return false;
    }

    /// <summary>
    /// Checks if a message exists in the inbox or has been processed.
    /// </summary>
    /// <param name="senderId">The sender grain ID.</param>
    /// <param name="messageId">The message ID.</param>
    /// <returns>True if the message is in the inbox or processed dictionary; otherwise, false.</returns>
    public bool ContainsOrProcessed(GrainId senderId, Guid messageId)
    {
        var key = (senderId, messageId);
        return _inbox.ContainsKey(key) || _processed.ContainsKey(key);
    }

    /// <summary>
    /// Marks a message as processed (for deduplication tracking).
    /// </summary>
    /// <param name="senderId">The sender grain ID.</param>
    /// <param name="messageId">The message ID.</param>
    public void MarkProcessed(GrainId senderId, Guid messageId)
    {
        var key = (senderId, messageId);
        _processed[key] = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Registers a handler that will be evaluated using its CanHandle method.
    /// Handlers are evaluated in registration order (first-match-wins).
    /// </summary>
    /// <param name="handler">The handler implementation.</param>
    public void RegisterHandler(IInboxHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        _handlers.Add(handler);

    }

    /// <summary>
    /// Tries to find a handler for the given context by calling CanHandle on registered handlers.
    /// Returns the first handler that returns true from CanHandle.
    /// </summary>
    /// <param name="context">The inbox handler context containing envelope metadata.</param>
    /// <param name="handler">The handler if found; otherwise, null.</param>
    /// <returns>True if a handler was found; otherwise, false.</returns>
    public bool TryFindHandler(IInboxHandlerContext context, [MaybeNullWhen(false)] out IInboxHandler handler)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Linear scan through handlers in registration order
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
    /// Registers a handler for a specific route (legacy method).
    /// </summary>
    /// <param name="routeKey">The route key to handle.</param>
    /// <param name="handler">The handler implementation.</param>
    public void RegisterHandler(string routeKey, IInboxHandler handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeKey);
        ArgumentNullException.ThrowIfNull(handler);

        if (_legacyRouteHandlers.ContainsKey(routeKey))
        {
            var wrapper = _handlers
                .OfType<LegacyRouteKeyHandlerWrapper>()
                .Single(candidate => string.Equals(candidate.RouteKey, routeKey, StringComparison.Ordinal));
            wrapper.Replace(handler);
        }
        else
        {
            _handlers.Add(new LegacyRouteKeyHandlerWrapper(routeKey, handler));
        }

        _legacyRouteHandlers[routeKey] = handler;
    }

    /// <summary>
    /// Checks if a route has a registered handler (legacy method).
    /// </summary>
    /// <param name="routeKey">The route key to check.</param>
    /// <returns>True if a handler is registered for this route; otherwise, false.</returns>
    public bool HasHandler(string routeKey)
    {
        return _legacyRouteHandlers.ContainsKey(routeKey);
    }

    /// <summary>
    /// Tries to get a handler for a specific route (legacy method).
    /// </summary>
    /// <param name="routeKey">The route key to get the handler for.</param>
    /// <param name="handler">The handler if found.</param>
    /// <returns>True if a handler is registered for this route; otherwise, false.</returns>
    public bool TryGetHandler(string routeKey, [MaybeNullWhen(false)] out IInboxHandler handler)
    {
        return _legacyRouteHandlers.TryGetValue(routeKey, out handler);
    }

    public async Task OnStart(CancellationToken cancellationToken = default)
    {
        if (!cancellationToken.IsCancellationRequested && _serviceProvider is not null)
        {
            _extension = _serviceProvider.GetRequiredService<DurableInboxExtension>();
            await _extension.ResumeProcessingAsync().ConfigureAwait(true);
        }
    }

    public async Task OnStop(CancellationToken cancellationToken = default)
    {
        if (_extension is not null)
        {
            await _extension.OnStop(cancellationToken).ConfigureAwait(true);
        }
    }
}

/// <summary>
/// Internal wrapper that adapts legacy route-based handlers to the new CanHandle pattern.
/// </summary>
internal sealed class LegacyRouteKeyHandlerWrapper : IInboxHandler
{
    private readonly string _routeKey;
    private IInboxHandler _innerHandler;

    public LegacyRouteKeyHandlerWrapper(string routeKey, IInboxHandler innerHandler)
    {
        _routeKey = routeKey;
        _innerHandler = innerHandler;
    }

    internal string RouteKey => _routeKey;

    internal void Replace(IInboxHandler handler) => _innerHandler = handler;

    public bool CanHandle(IInboxHandlerContext context)
    {
        return context.Envelope.RouteKey == _routeKey;
    }

    public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        return _innerHandler.HandleAsync(context, cancellationToken);
    }
}
