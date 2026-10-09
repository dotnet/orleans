using System;

namespace Orleans.DurableMessaging;

/// <summary>Extensions for registering typed, subject-based inbox handlers.</summary>
public static class DurableInboxExtensions
{
    /// <summary>Configures and registers one dispatcher for an inbox's typed subjects.</summary>
    /// <param name="inbox">The inbox receiving the handler registration.</param>
    /// <param name="configure">The action registering subjects and their typed handlers.</param>
    /// <remarks>
    /// Registration freezes when configuration succeeds. Each handler receives a decoded body
    /// and follows the preparation, synchronous final-block, and explicit completion contract
    /// of <see cref="IInboxHandler.HandleAsync"/>.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// No subjects are configured, a subject is duplicated, or the inbox already has a handler.
    /// </exception>
    public static void RegisterHandlers(this IDurableInbox inbox, Action<DurableInboxDispatcher> configure)
    {
        ArgumentNullException.ThrowIfNull(inbox);
        ArgumentNullException.ThrowIfNull(configure);
        var dispatcher = new DurableInboxDispatcher();
        configure(dispatcher);
        dispatcher.FreezeRegistration();
        inbox.RegisterHandler(dispatcher);
    }
}
