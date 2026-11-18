namespace Orleans.Messaging;

/// <summary>Registers handlers for the messages delivered to a grain's inbox.</summary>
/// <remarks>
/// <para>
/// Each message is dispatched to the first registered handler which can handle it. Handlers can be registered at any
/// time. <see cref="InboxExtensions"/> registers typed subject handlers.
/// </para>
/// <para>
/// A grain receives messages by resolving its inbox during construction or activation setup, typically by constructor
/// injection, so the inbox recovers with the grain's journal.
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

    /// <summary>
    /// Registers a callback which reports durable work held outside the inbox, such as messages which an
    /// <see cref="IInboxMessageSink"/> accepted, so the messaging wake-up reactivates the grain while that work remains.
    /// </summary>
    /// <param name="getWakeupDelay">
    /// Returns <see langword="null"/> when no work is pending; otherwise, the longest acceptable delay before a
    /// deactivated grain is reactivated to resume the work. A delay of <see cref="System.TimeSpan.Zero"/> or less requests
    /// a prompt wake-up.
    /// </param>
    /// <returns>This inbox.</returns>
    /// <remarks>
    /// <para>
    /// The callback runs synchronously on the activation scheduler before each journal write and on each wake-up. A write
    /// which leaves work pending schedules the wake-up before it is stored, so a deactivation or crash can't strand the
    /// work. Each wake-up reschedules after the shortest reported delay, and wake-ups end once no callback reports work
    /// and no messages are pending. Wake-ups may be early, late, or duplicated.
    /// </para>
    /// <para>
    /// Hold the work in journaled state of the grain's journal and resume it when the grain activates. Register the
    /// callback during construction or activation setup, before the grain activates.
    /// </para>
    /// </remarks>
    IInbox RegisterWakeup(System.Func<System.TimeSpan?> getWakeupDelay);
}
