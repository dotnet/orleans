using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Orleans.Journaling;
using Orleans.Runtime;

namespace Orleans.DurableMessaging;

/// <summary>
/// Provides access to journaled pending messages and their capacity limit.
/// Registers the single handler for this inbox.
/// </summary>
internal sealed class DurableInbox : IDurableInbox
{
    private readonly IDurableDictionary<HierarchicalKey, DurableEnvelope> _inbox;
    private IInboxHandler? _handler;
    private readonly int _capacity;

    /// <summary>
    /// Creates an inbox over journaled message storage.
    /// </summary>
    /// <param name="inbox">Durable dictionary for storing unprocessed messages.</param>
    /// <param name="capacity">Maximum inbox capacity (default: 1000).</param>
    public DurableInbox(
        IDurableDictionary<HierarchicalKey, DurableEnvelope> inbox,
        int capacity = 1000)
    {
        ArgumentNullException.ThrowIfNull(inbox);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        _inbox = inbox;
        _capacity = capacity;
    }

    internal DurableInbox(
        IDurableDictionary<HierarchicalKey, DurableEnvelope> inbox,
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
    /// Gets the maximum inbox capacity.
    /// </summary>
    public int Capacity => _capacity;

    /// <summary>
    /// Gets all pending messages (no ordering guarantee).
    /// </summary>
    public IEnumerable<DurableEnvelope> Messages => _inbox.Values;

    /// <summary>
    /// Tries to get a specific message by its key.
    /// </summary>
    /// <param name="messageId">The receiver-local command identity.</param>
    /// <param name="envelope">The envelope if found.</param>
    /// <returns>True if the message exists in the inbox; otherwise, false.</returns>
    public bool TryGetMessage(HierarchicalKey messageId, [MaybeNullWhen(false)] out DurableEnvelope envelope) =>
        _inbox.TryGetValue(messageId, out envelope);

    /// <summary>
    /// Registers the single handler for this inbox.
    /// </summary>
    public void RegisterHandler(IInboxHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (_handler is not null)
        {
            throw new InvalidOperationException("A handler is already registered for this durable inbox.");
        }

        _handler = handler;
    }

    internal bool TryGetHandler([MaybeNullWhen(false)] out IInboxHandler handler)
    {
        handler = _handler;
        return handler is not null;
    }
}
