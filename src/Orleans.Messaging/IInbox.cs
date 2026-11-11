namespace Orleans.Messaging;

/// <summary>Registers handlers for the messages delivered to a grain's inbox.</summary>
/// <remarks>
/// <para>
/// Each message is dispatched to the first registered handler which can handle it. Handlers can be registered at any
/// time. <see cref="InboxExtensions"/> registers typed subject handlers.
/// </para>
/// <para>
/// A handler decodes the body, performs asynchronous I/O, validates local results, and checks cancellation before
/// the first shared business or journaled mutation. From that mutation until it returns, it executes synchronously:
/// it applies safe-to-commit business changes, stages outgoing messages through <see cref="IOutbox.Send(OutboxMessage)"/>, and calls
/// <see cref="IInboxHandlerContext.Complete"/> or <see cref="IInboxHandlerContext.Fail"/>. The runtime persists the
/// outcome with those changes. Failures before a terminal operation follow the inbox retry and dead-letter policy.
/// </para>
/// </remarks>
public interface IInbox
{
    /// <summary>Registers a handler.</summary>
    /// <param name="handler">The handler.</param>
    /// <returns>This inbox.</returns>
    IInbox Register(IInboxHandler handler);

    /// <summary>Registers a sink which accepts messages directly into application-owned journaled state.</summary>
    /// <param name="sink">The sink.</param>
    /// <returns>This inbox.</returns>
    /// <remarks>
    /// Messages are offered to sinks before inbox acceptance and handler dispatch; see <see cref="IInboxMessageSink"/>.
    /// Register sinks during construction or activation setup, before the grain receives messages.
    /// </remarks>
    IInbox Register(IInboxMessageSink sink);

    /// <summary>Registers a source of durable work which the inbox's wake-up keeps reactivating the grain for.</summary>
    /// <param name="source">The work source.</param>
    /// <returns>This inbox.</returns>
    /// <remarks>
    /// See <see cref="IInboxWorkSource"/>. Register work sources during construction or activation setup, before the
    /// grain activates.
    /// </remarks>
    IInbox Register(IInboxWorkSource source);
}
