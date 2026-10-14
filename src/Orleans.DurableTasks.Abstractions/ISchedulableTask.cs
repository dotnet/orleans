namespace Orleans.DurableTasks;

/// <summary>Defines a durable task which a host can schedule independently.</summary>
public interface ISchedulableTask
{
    /// <summary>
    /// Schedules or reattaches to this definition under <paramref name="taskId"/>.
    /// An existing identifier returns its recorded response and retains its original definition.
    /// </summary>
    ValueTask<DurableTaskResponse> ScheduleAsync(TaskId taskId, CancellationToken cancellationToken);

    /// <summary>Gets a handle for a scheduled identifier.</summary>
    IScheduledTaskHandle GetHandle(TaskId taskId);

    /// <summary>
    /// Starts this definition as a detached root task under <paramref name="taskId"/>, which no caller observes.
    /// </summary>
    /// <remarks>
    /// The returned operation completes once the host has durably accepted the schedule. The host sends the scheduler no
    /// result, and it retains the task's result for polling as it does for other roots without an observing caller.
    /// Detaching an existing identifier with the same definition leaves the task unchanged. Hosts which don't support
    /// detached tasks keep this default implementation, which throws <see cref="NotSupportedException"/>.
    /// </remarks>
    ValueTask DetachAsync(TaskId taskId, CancellationToken cancellationToken)
        => throw new NotSupportedException("The durable task definition does not support detached scheduling.");
}

/// <summary>Controls and observes one scheduled durable task.</summary>
public interface IScheduledTaskHandle
{
    /// <summary>Gets the scheduled task identifier.</summary>
    TaskId TaskId { get; }

    /// <summary>
    /// Waits for a terminal response. Canceling <paramref name="cancellationToken"/> cancels this wait.
    /// </summary>
    ValueTask<DurableTaskResponse> WaitAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Observes the current response. Canceling <paramref name="cancellationToken"/> cancels this poll.
    /// </summary>
    ValueTask<DurableTaskResponse> PollAsync(PollingOptions options, CancellationToken cancellationToken);

    /// <summary>
    /// Durably requests task cancellation. The request is monotonic and idempotent. The token abandons
    /// waiting for acknowledgement.
    /// </summary>
    /// <remarks>
    /// The operation completes once the host has durably recorded the request; the task's response reports the outcome.
    /// A running task observes the request through its durable cancellation token and decides its own outcome.
    /// </remarks>
    ValueTask CancelAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Administratively aborts the task: the host terminalizes it and its attached descendants as failed with
    /// <see cref="DurableTaskAbortedException"/>, without running further task code. The token abandons waiting for
    /// acknowledgement.
    /// </summary>
    /// <param name="reason">The reason for the abort, recorded as the exception message.</param>
    /// <param name="cancellationToken">A token which abandons waiting for the abort to commit.</param>
    /// <remarks>
    /// Aborting a terminal task leaves it unchanged. Hosts which don't support abort keep this default implementation,
    /// which throws <see cref="NotSupportedException"/>.
    /// </remarks>
    ValueTask AbortAsync(string reason, CancellationToken cancellationToken)
        => throw new NotSupportedException("The durable task host does not support aborting tasks.");
}

/// <summary>Configures a polling operation.</summary>
public readonly struct PollingOptions : IEquatable<PollingOptions>
{
    private readonly TimeSpan? _pollTimeout;

    /// <summary>Gets the timeout used by default polling options.</summary>
    public static TimeSpan DefaultPollTimeout => TimeSpan.FromSeconds(5);

    /// <summary>Gets or initializes the maximum duration of the poll.</summary>
    public TimeSpan PollTimeout
    {
        get => _pollTimeout ?? DefaultPollTimeout;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero);
            _pollTimeout = value;
        }
    }

    /// <inheritdoc />
    public bool Equals(PollingOptions other) => PollTimeout == other.PollTimeout;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PollingOptions other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => PollTimeout.GetHashCode();

    /// <summary>Returns whether two polling configurations are equivalent.</summary>
    public static bool operator ==(PollingOptions left, PollingOptions right) => left.Equals(right);

    /// <summary>Returns whether two polling configurations differ.</summary>
    public static bool operator !=(PollingOptions left, PollingOptions right) => !left.Equals(right);
}
