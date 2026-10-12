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
/// it applies safe-to-commit business changes, stages outgoing messages through <see cref="IOutbox.Send"/>, and calls
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
}
