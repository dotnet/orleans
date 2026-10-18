using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Messaging;

/// <summary>Dispatches each inbox message to the first registered handler which can handle it.</summary>
internal readonly struct InboxDispatcher
{
    private readonly List<IInboxHandler> _handlers;

    internal InboxDispatcher(List<IInboxHandler> handlers) => _handlers = handlers;

    internal bool IsEmpty => _handlers.Count == 0;

    internal void Add(IInboxHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handlers.Add(handler);
    }

    internal ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var handler in _handlers)
        {
            if (handler.CanHandle(context.Message))
            {
                return handler.HandleAsync(context, cancellationToken);
            }
        }

        throw new InvalidOperationException(
            $"No handler is registered for message subject '{context.Message.Envelope.Subject}'.");
    }
}
