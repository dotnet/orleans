using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Orleans.Journaling.Messaging;

/// <summary>
/// Durable inbox implementation for receiving and processing messages.
/// Uses IDurableDictionary for persistent storage with deduplication support.
/// </summary>
internal sealed class DurableInbox : IDurableInbox
{
    private readonly IDurableDictionary<(GrainId SenderId, Guid MessageId), DurableEnvelope> _inbox;
    private readonly IDurableDictionary<(GrainId SenderId, Guid MessageId), DateTimeOffset> _processed;
    private readonly Dictionary<string, IInboxHandler> _handlers;
    private readonly int _capacity;

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
        _handlers = new Dictionary<string, IInboxHandler>();
        _capacity = capacity;
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
        
        // Get the envelope before removing to dispose its data
        if (_inbox.TryGetValue(key, out var envelope))
        {
            var removed = _inbox.Remove(key);
            if (removed)
            {
                // Dispose ArcBuffer resources
                envelope.Data.Dispose();
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
    /// Registers a handler for a specific route.
    /// </summary>
    /// <param name="routeKey">The route key to handle.</param>
    /// <param name="handler">The handler implementation.</param>
    public void RegisterHandler(string routeKey, IInboxHandler handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeKey);
        ArgumentNullException.ThrowIfNull(handler);

        _handlers[routeKey] = handler;
    }

    /// <summary>
    /// Checks if a route has a registered handler.
    /// </summary>
    /// <param name="routeKey">The route key to check.</param>
    /// <returns>True if a handler is registered for this route; otherwise, false.</returns>
    public bool HasHandler(string routeKey)
    {
        return _handlers.ContainsKey(routeKey);
    }

    /// <summary>
    /// Tries to get a handler for a specific route.
    /// </summary>
    /// <param name="routeKey">The route key to get the handler for.</param>
    /// <param name="handler">The handler if found.</param>
    /// <returns>True if a handler is registered for this route; otherwise, false.</returns>
    public bool TryGetHandler(string routeKey, [MaybeNullWhen(false)] out IInboxHandler handler)
    {
        return _handlers.TryGetValue(routeKey, out handler);
    }
}
