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
/// when installed using <see cref="DurableInboxExtensions.RegisterHandlers"/> or handling begins.
/// Each delegate follows <see cref="IInboxHandler.HandleAsync"/>'s preparation,
/// synchronous final-block, and explicit completion contract.
/// </remarks>
public sealed class DurableInboxDispatcher : IInboxHandler
{
    private readonly Dictionary<string, IHandler> _handlers = new(StringComparer.Ordinal);
    private bool _started;

    /// <summary>Adds a subject's synchronous typed handler before handling begins.</summary>
    /// <typeparam name="T">The payload contract.</typeparam>
    /// <param name="messageType">The subject and serializer binding.</param>
    /// <param name="handler">The synchronous delegate receiving the decoded body and context.</param>
    /// <returns>This dispatcher.</returns>
    /// <remarks>
    /// The attempt token is checked after decoding and before entering the delegate. The delegate
    /// runs synchronously and explicitly calls <see cref="IInboxHandlerContext.Complete"/>.
    /// Use the task-returning overload for asynchronous preparation or application cancellation checks.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidOperationException">Registration is frozen or the subject is already registered.</exception>
    public DurableInboxDispatcher Register<T>(
        DurableMessageType<T> messageType,
        Action<T, IInboxHandlerContext> handler)
    {
        ArgumentNullException.ThrowIfNull(messageType);
        ArgumentNullException.ThrowIfNull(handler);
        return Register(messageType, (body, context, token) =>
        {
            token.ThrowIfCancellationRequested();
            handler(body, context);
            return ValueTask.CompletedTask;
        });
    }

    /// <summary>Adds a subject's typed handler before handling begins.</summary>
    /// <typeparam name="T">The payload contract.</typeparam>
    /// <param name="messageType">The subject and serializer binding.</param>
    /// <param name="handler">The delegate receiving the decoded body, context, and attempt token.</param>
    /// <returns>This dispatcher.</returns>
    /// <exception cref="InvalidOperationException">Registration is frozen or the subject is already registered.</exception>
    public DurableInboxDispatcher Register<T>(
        DurableMessageType<T> messageType,
        Func<T, IInboxHandlerContext, CancellationToken, ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(messageType);
        ArgumentNullException.ThrowIfNull(handler);
        if (_started)
        {
            throw new InvalidOperationException("Durable inbox dispatcher registration is frozen.");
        }

        if (!_handlers.TryAdd(messageType.Subject, new Handler<T>(messageType, handler)))
        {
            throw new InvalidOperationException($"A handler is already registered for durable message subject '{messageType.Subject}'.");
        }

        return this;
    }

    internal void FreezeRegistration()
    {
        if (_handlers.Count == 0)
        {
            throw new InvalidOperationException("Register at least one durable message subject.");
        }

        _started = true;
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
