using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.DurableMessaging;

/// <summary>Dispatches exact subjects to typed application handlers.</summary>
/// <remarks>
/// Configure once per activation and register as the inbox's single handler. Registration freezes
/// when installed using <see cref="DurableInboxExtensions.RegisterHandlers"/> or handling begins.
/// Handlers follow <see cref="IInboxHandler.HandleAsync"/>'s synchronous final-block and explicit
/// completion contract. Stateful overloads store arguments directly and support static delegates.
/// </remarks>
public sealed class DurableInboxDispatcher : IInboxHandler
{
    private readonly List<IHandler> _handlers = [];
    private Dictionary<string, IHandler>? _dispatch;
    private bool _started;

    /// <summary>Registers a synchronous subject handler.</summary>
    /// <typeparam name="T">The body contract.</typeparam>
    /// <param name="messageType">The subject and serializer binding.</param>
    /// <param name="handler">The synchronous body and context delegate.</param>
    /// <returns>This dispatcher.</returns>
    public DurableInboxDispatcher Register<T>(
        DurableMessageType<T> messageType, Action<T, IInboxHandlerContext> handler)
    {
        ArgumentNullException.ThrowIfNull(messageType);
        ArgumentNullException.ThrowIfNull(handler);
        return Add(new SynchronousHandler<T>(messageType, handler));
    }

    /// <summary>Registers a synchronous subject handler with an explicit argument.</summary>
    /// <typeparam name="T">The body contract.</typeparam>
    /// <typeparam name="TArg">The handler argument type.</typeparam>
    /// <param name="messageType">The subject and serializer binding.</param>
    /// <param name="argument">The argument stored with the handler.</param>
    /// <param name="handler">The synchronous body, argument, and context delegate.</param>
    /// <returns>This dispatcher.</returns>
    /// <remarks>Use a static delegate to pass state without a captured closure.</remarks>
    public DurableInboxDispatcher Register<T, TArg>(
        DurableMessageType<T> messageType, TArg argument, Action<T, TArg, IInboxHandlerContext> handler)
    {
        ArgumentNullException.ThrowIfNull(messageType);
        ArgumentNullException.ThrowIfNull(handler);
        return Add(new SynchronousHandler<T, TArg>(messageType, argument, handler));
    }

    /// <summary>Registers a subject handler supporting asynchronous preparation.</summary>
    /// <typeparam name="T">The body contract.</typeparam>
    /// <param name="messageType">The subject and serializer binding.</param>
    /// <param name="handler">The body, context, and attempt-token delegate.</param>
    /// <returns>This dispatcher.</returns>
    public DurableInboxDispatcher Register<T>(
        DurableMessageType<T> messageType, Func<T, IInboxHandlerContext, CancellationToken, ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(messageType);
        ArgumentNullException.ThrowIfNull(handler);
        return Add(new AsynchronousHandler<T>(messageType, handler));
    }

    /// <summary>Registers an asynchronous subject handler with an explicit argument.</summary>
    /// <typeparam name="T">The body contract.</typeparam>
    /// <typeparam name="TArg">The handler argument type.</typeparam>
    /// <param name="messageType">The subject and serializer binding.</param>
    /// <param name="argument">The argument stored with the handler.</param>
    /// <param name="handler">The body, argument, context, and attempt-token delegate.</param>
    /// <returns>This dispatcher.</returns>
    /// <remarks>Use a static delegate to pass state without a captured closure.</remarks>
    public DurableInboxDispatcher Register<T, TArg>(
        DurableMessageType<T> messageType, TArg argument,
        Func<T, TArg, IInboxHandlerContext, CancellationToken, ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(messageType);
        ArgumentNullException.ThrowIfNull(handler);
        return Add(new AsynchronousHandler<T, TArg>(messageType, argument, handler));
    }

    private DurableInboxDispatcher Add(IHandler handler)
    {
        if (_started)
        {
            throw new InvalidOperationException("Durable inbox dispatcher registration is frozen.");
        }

        foreach (var existing in _handlers)
        {
            if (string.Equals(existing.Subject, handler.Subject, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"A handler is already registered for durable message subject '{handler.Subject}'.");
            }
        }

        _handlers.Add(handler);
        return this;
    }

    internal void FreezeRegistration()
    {
        if (_handlers.Count == 0)
        {
            throw new InvalidOperationException("Register at least one durable message subject.");
        }

        Freeze();
    }

    private void Freeze()
    {
        if (_started) return;
        _dispatch = new Dictionary<string, IHandler>(_handlers.Count, StringComparer.Ordinal);
        foreach (var handler in _handlers) _dispatch.Add(handler.Subject, handler);
        _started = true;
    }

    /// <inheritdoc/>
    public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        Freeze();
        cancellationToken.ThrowIfCancellationRequested();
        if (!_dispatch!.TryGetValue(context.Envelope.Subject, out var handler))
        {
            throw new InvalidOperationException($"No handler is registered for durable message subject '{context.Envelope.Subject}'.");
        }

        return handler.HandleAsync(context, cancellationToken);
    }

    private interface IHandler
    {
        string Subject { get; }
        ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken);
    }

    private abstract class Handler<T>(DurableMessageType<T> messageType) : IHandler
    {
        public string Subject => messageType.Subject;

        public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            var body = messageType.Decode(context.Envelope);
            cancellationToken.ThrowIfCancellationRequested();
            return Invoke(body, context, cancellationToken);
        }

        protected abstract ValueTask Invoke(T body, IInboxHandlerContext context, CancellationToken cancellationToken);
    }

    private sealed class SynchronousHandler<T>(
        DurableMessageType<T> messageType, Action<T, IInboxHandlerContext> handler) : Handler<T>(messageType)
    {
        protected override ValueTask Invoke(T body, IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            handler(body, context);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SynchronousHandler<T, TArg>(
        DurableMessageType<T> messageType, TArg argument, Action<T, TArg, IInboxHandlerContext> handler) : Handler<T>(messageType)
    {
        protected override ValueTask Invoke(T body, IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            handler(body, argument, context);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class AsynchronousHandler<T>(
        DurableMessageType<T> messageType,
        Func<T, IInboxHandlerContext, CancellationToken, ValueTask> handler) : Handler<T>(messageType)
    {
        protected override ValueTask Invoke(T body, IInboxHandlerContext context, CancellationToken cancellationToken) =>
            handler(body, context, cancellationToken);
    }

    private sealed class AsynchronousHandler<T, TArg>(
        DurableMessageType<T> messageType, TArg argument,
        Func<T, TArg, IInboxHandlerContext, CancellationToken, ValueTask> handler) : Handler<T>(messageType)
    {
        protected override ValueTask Invoke(T body, IInboxHandlerContext context, CancellationToken cancellationToken) =>
            handler(body, argument, context, cancellationToken);
    }
}
