namespace Orleans.DurableTasks;

/// <summary>Describes how a durable execution starts a task, for hosts which implement <see cref="DurableTaskContext"/>.</summary>
[Flags]
public enum DurableTaskStartOptions
{
    /// <summary>The task is an attached child, which the starting execution owns.</summary>
    None = 0,

    /// <summary>
    /// The task is an independent root task, which the starting execution observes as a subscriber. No cancellation,
    /// abort, or deletion of the starting execution reaches it.
    /// </summary>
    Detached = 1,

    /// <summary>
    /// The start completes once the task's host has accepted and durably stored the task, or the task's result is known.
    /// </summary>
    WaitForAcceptance = 2,
}
