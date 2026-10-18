namespace Orleans.DurableTasks;

/// <summary>Identifies why a host canceled an attempt of a durable task.</summary>
public enum DurableTaskAttemptCancellationReason
{
    /// <summary>The grain activation which runs the attempt is stopping. The task replays in its next activation.</summary>
    ActivationStopping,

    /// <summary>The task was administratively aborted, and its outcome is a <see cref="DurableTaskAbortedException"/>.</summary>
    TaskAborted,

    /// <summary>The task was deleted together with its execution tree.</summary>
    TaskDeleted,

    /// <summary>
    /// The task's execution recorded a version of a change which the running code doesn't support. The task stays pending
    /// until code which supports the version replays it; the <see cref="DurableTaskAttemptCanceledException"/>'s inner
    /// exception is a <see cref="DurableTaskVersionException"/>.
    /// </summary>
    UnsupportedVersion,
}

/// <summary>
/// The exception which every durable operation of a canceled attempt throws, so the attempt's code unwinds and releases its
/// in-process resources.
/// </summary>
/// <remarks>
/// <para>
/// An attempt is one in-memory run of a task's body. A host cancels the attempt, rather than the task, when the attempt's
/// work can't be recorded any more: its grain activation is stopping, or the task was aborted or deleted. From then on,
/// the attempt's durable operations, such as <see cref="DurableTask.RunInline{TResult}(string, Func{TResult})"/>, starting or
/// awaiting tasks, signals, delays, and combinators, throw this exception, and so do changes to the grain's journaled
/// state. The host records nothing the attempt does after its cancellation: a stopping activation's task replays in a new
/// attempt, and an aborted or deleted task already has its outcome.
/// </para>
/// <para>
/// <see cref="DurableTaskContext.AttemptCancellationToken"/> and <see cref="DurableTaskContext.CancellationToken"/> are
/// canceled together with the attempt. <see cref="DurableTaskContext.IsTaskCancellationRequested"/> reports only durable
/// task cancellation, so a <see langword="catch"/> block filtered on it runs durable cleanup only for task cancellation.
/// Release in-process resources, such as semaphores and disposables, in <see langword="finally"/> blocks and
/// <see langword="using"/> statements, which run for both.
/// </para>
/// </remarks>
public sealed class DurableTaskAttemptCanceledException : OperationCanceledException
{
    /// <summary>Initializes an exception for an attempt canceled because its activation is stopping.</summary>
    public DurableTaskAttemptCanceledException()
        : this("The durable task attempt was canceled.")
    {
    }

    /// <summary>Initializes an exception with <paramref name="message"/>, for an attempt canceled because its activation is stopping.</summary>
    /// <param name="message">The message which describes the cancellation.</param>
    public DurableTaskAttemptCanceledException(string? message)
        : this(message, innerException: null)
    {
    }

    /// <summary>Initializes an exception with <paramref name="message"/>, for an attempt canceled because its activation is stopping.</summary>
    /// <param name="message">The message which describes the cancellation.</param>
    /// <param name="innerException">The exception which the canceled operation threw.</param>
    public DurableTaskAttemptCanceledException(string? message, Exception? innerException)
        : this(message, innerException, DurableTaskAttemptCancellationReason.ActivationStopping, CancellationToken.None)
    {
    }

    /// <summary>Initializes an exception for an attempt canceled for <paramref name="reason"/>, with a message which describes it.</summary>
    /// <param name="reason">Why the host canceled the attempt.</param>
    /// <param name="token">The attempt's cancellation token.</param>
    /// <param name="innerException">The exception which the canceled operation threw, if any.</param>
    public DurableTaskAttemptCanceledException(
        DurableTaskAttemptCancellationReason reason,
        CancellationToken token,
        Exception? innerException = null)
        : this(GetMessage(reason), innerException, reason, token)
    {
    }

    /// <summary>Initializes an exception for an attempt canceled for <paramref name="reason"/>.</summary>
    /// <param name="message">The message which describes the cancellation.</param>
    /// <param name="innerException">The exception which the canceled operation threw.</param>
    /// <param name="reason">Why the host canceled the attempt.</param>
    /// <param name="token">The attempt's cancellation token.</param>
    public DurableTaskAttemptCanceledException(
        string? message,
        Exception? innerException,
        DurableTaskAttemptCancellationReason reason,
        CancellationToken token)
        : base(message, innerException, token)
    {
        Reason = reason;
    }

    /// <summary>Gets why the host canceled the attempt.</summary>
    public DurableTaskAttemptCancellationReason Reason { get; }

    private static string GetMessage(DurableTaskAttemptCancellationReason reason) => reason switch
    {
        DurableTaskAttemptCancellationReason.ActivationStopping => "The durable task attempt was canceled because its grain activation is stopping. The task continues in a new attempt.",
        DurableTaskAttemptCancellationReason.TaskAborted => "The durable task attempt was canceled because the task was aborted.",
        DurableTaskAttemptCancellationReason.TaskDeleted => "The durable task attempt was canceled because the task was deleted with its execution tree.",
        DurableTaskAttemptCancellationReason.UnsupportedVersion => "The durable task attempt was canceled because its execution recorded a version which the running code doesn't support. The task stays pending until code which supports the version replays it.",
        _ => "The durable task attempt was canceled.",
    };
}
