using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.DurableMessaging;

/// <summary>
/// Dispatches exact subjects to typed application handlers.
/// </summary>
/// <remarks>
/// Configure once per activation and register as the inbox's single handler. Registration freezes
/// when handling begins. Each delegate follows <see cref="IInboxHandler.HandleAsync"/>'s preparation,
/// synchronous final-block, and explicit completion contract.
/// </remarks>
public sealed class DurableInboxDispatcher : IInboxHandler
{
    private readonly Dictionary<string, IHandler> _handlers = new(StringComparer.Ordinal);
    private bool _started;

    /// <summary>Adds a subject's typed handler before handling begins.</summary>
    /// <typeparam name="T">The payload contract.</typeparam>
    /// <param name="messageType">The subject and serializer binding.</param>
    /// <param name="handler">The delegate receiving the decoded body, context, and attempt token.</param>
    /// <returns>This dispatcher.</returns>
    /// <exception cref="InvalidOperationException">Handling has begun or the subject is already registered.</exception>
    public DurableInboxDispatcher Register<T>(
        DurableMessageType<T> messageType,
        Func<T, IInboxHandlerContext, CancellationToken, ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(messageType);
        ArgumentNullException.ThrowIfNull(handler);
        if (_started)
        {
            throw new InvalidOperationException("Durable inbox dispatcher registration is frozen after handling begins.");
        }

        if (!_handlers.TryAdd(messageType.Subject, new Handler<T>(messageType, handler)))
        {
            throw new InvalidOperationException($"A handler is already registered for durable message subject '{messageType.Subject}'.");
        }

        return this;
    }

    /// <inheritdoc/>
    public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        _started = true;
        cancellationToken.ThrowIfCancellationRequested();
        if (!_handlers.TryGetValue(context.Envelope.Subject, out var handler))
        {
            throw new InvalidOperationException($"No handler is registered for durable message subject '{context.Envelope.Subject}'.");
        }

        return handler.HandleAsync(context, cancellationToken);
    }

    private interface IHandler
    {
        ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken);
    }

    private sealed class Handler<T>(
        DurableMessageType<T> messageType,
        Func<T, IInboxHandlerContext, CancellationToken, ValueTask> handler) : IHandler
    {
        public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken) =>
            handler(messageType.Decode(context.Envelope), context, cancellationToken);
    }
}
