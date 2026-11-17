using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Messaging;

/// <summary>Registers typed subject handlers with an <see cref="IInbox"/>.</summary>
/// <remarks>Stateful overloads store their argument directly and support static delegates.</remarks>
public static class InboxExtensions
{
    /// <summary>Registers a synchronous subject handler.</summary>
    /// <typeparam name="T">The body contract.</typeparam>
    /// <param name="inbox">The inbox.</param>
    /// <param name="messageType">The subject and serializer binding.</param>
    /// <param name="handler">The synchronous body and context delegate.</param>
    /// <returns>The inbox.</returns>
    public static IInbox Register<T>(this IInbox inbox, MessageType<T> messageType, Action<T, IInboxHandlerContext> handler)
    {
        ArgumentNullException.ThrowIfNull(inbox);
        ArgumentNullException.ThrowIfNull(messageType);
        ArgumentNullException.ThrowIfNull(handler);
        return inbox.Register(new SynchronousHandler<T>(messageType, handler));
    }

    /// <summary>Registers a synchronous subject handler with an explicit argument.</summary>
    /// <typeparam name="T">The body contract.</typeparam>
    /// <typeparam name="TArg">The handler argument type.</typeparam>
    /// <param name="inbox">The inbox.</param>
    /// <param name="messageType">The subject and serializer binding.</param>
    /// <param name="argument">The argument stored with the handler.</param>
    /// <param name="handler">The synchronous body, argument, and context delegate.</param>
    /// <returns>The inbox.</returns>
    /// <remarks>Use a static delegate to pass state without a captured closure.</remarks>
    public static IInbox Register<T, TArg>(
        this IInbox inbox, MessageType<T> messageType, TArg argument, Action<T, TArg, IInboxHandlerContext> handler)
    {
        ArgumentNullException.ThrowIfNull(inbox);
        ArgumentNullException.ThrowIfNull(messageType);
        ArgumentNullException.ThrowIfNull(handler);
        return inbox.Register(new SynchronousHandler<T, TArg>(messageType, argument, handler));
    }

    /// <summary>Registers a subject handler supporting asynchronous preparation.</summary>
    /// <typeparam name="T">The body contract.</typeparam>
    /// <param name="inbox">The inbox.</param>
    /// <param name="messageType">The subject and serializer binding.</param>
    /// <param name="handler">The body, context, and attempt-token delegate.</param>
    /// <returns>The inbox.</returns>
    public static IInbox Register<T>(
        this IInbox inbox, MessageType<T> messageType, Func<T, IInboxHandlerContext, CancellationToken, ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(inbox);
        ArgumentNullException.ThrowIfNull(messageType);
        ArgumentNullException.ThrowIfNull(handler);
        return inbox.Register(new AsynchronousHandler<T>(messageType, handler));
    }

    /// <summary>Registers an asynchronous subject handler with an explicit argument.</summary>
    /// <typeparam name="T">The body contract.</typeparam>
    /// <typeparam name="TArg">The handler argument type.</typeparam>
    /// <param name="inbox">The inbox.</param>
    /// <param name="messageType">The subject and serializer binding.</param>
    /// <param name="argument">The argument stored with the handler.</param>
    /// <param name="handler">The body, argument, context, and attempt-token delegate.</param>
    /// <returns>The inbox.</returns>
    /// <remarks>Use a static delegate to pass state without a captured closure.</remarks>
    public static IInbox Register<T, TArg>(
        this IInbox inbox, MessageType<T> messageType, TArg argument,
        Func<T, TArg, IInboxHandlerContext, CancellationToken, ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(inbox);
        ArgumentNullException.ThrowIfNull(messageType);
        ArgumentNullException.ThrowIfNull(handler);
        return inbox.Register(new AsynchronousHandler<T, TArg>(messageType, argument, handler));
    }

    private abstract class Handler<T>(MessageType<T> messageType) : IInboxHandler
    {
        public bool CanHandle(InboxMessage message) =>
            string.Equals(message.Envelope.Subject, messageType.Subject, StringComparison.Ordinal);

        public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The delegate owns the decoded body, including when cancellation arrives during decoding.
            var body = messageType.Decode(context.Message.Envelope);
            return Invoke(body, context, cancellationToken);
        }

        protected abstract ValueTask Invoke(T body, IInboxHandlerContext context, CancellationToken cancellationToken);
    }

    private sealed class SynchronousHandler<T>(
        MessageType<T> messageType, Action<T, IInboxHandlerContext> handler) : Handler<T>(messageType)
    {
        protected override ValueTask Invoke(T body, IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            handler(body, context);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SynchronousHandler<T, TArg>(
        MessageType<T> messageType, TArg argument, Action<T, TArg, IInboxHandlerContext> handler) : Handler<T>(messageType)
    {
        protected override ValueTask Invoke(T body, IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            handler(body, argument, context);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class AsynchronousHandler<T>(
        MessageType<T> messageType,
        Func<T, IInboxHandlerContext, CancellationToken, ValueTask> handler) : Handler<T>(messageType)
    {
        protected override ValueTask Invoke(T body, IInboxHandlerContext context, CancellationToken cancellationToken) =>
            handler(body, context, cancellationToken);
    }

    private sealed class AsynchronousHandler<T, TArg>(
        MessageType<T> messageType, TArg argument,
        Func<T, TArg, IInboxHandlerContext, CancellationToken, ValueTask> handler) : Handler<T>(messageType)
    {
        protected override ValueTask Invoke(T body, IInboxHandlerContext context, CancellationToken cancellationToken) =>
            handler(body, argument, context, cancellationToken);
    }
}
