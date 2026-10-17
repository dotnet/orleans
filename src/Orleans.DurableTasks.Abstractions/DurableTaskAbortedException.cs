namespace Orleans.DurableTasks;

/// <summary>The failure which a host records for a durable task which was administratively aborted.</summary>
/// <remarks>
/// <see cref="ScheduledTask.AbortAsync"/> terminalizes a task as failed with this exception, whose message is the reason
/// which the operator supplied. Awaiting the aborted task throws it.
/// </remarks>
public sealed class DurableTaskAbortedException : Exception
{
    /// <summary>Initializes an exception for a task aborted without a stated reason.</summary>
    public DurableTaskAbortedException() : base("The durable task was aborted.")
    {
    }

    /// <summary>Initializes an exception for a task aborted for <paramref name="reason"/>.</summary>
    /// <param name="reason">The reason the task was aborted, which becomes the exception message.</param>
    public DurableTaskAbortedException(string reason) : base(reason)
    {
    }

    /// <summary>Initializes an exception for a task aborted for <paramref name="reason"/>.</summary>
    /// <param name="reason">The reason the task was aborted, which becomes the exception message.</param>
    /// <param name="innerException">The exception which caused the abort.</param>
    public DurableTaskAbortedException(string reason, Exception? innerException) : base(reason, innerException)
    {
    }
}
