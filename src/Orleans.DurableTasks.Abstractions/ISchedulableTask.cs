namespace Orleans.DurableTasks;

/// <summary>Defines a durable task which a host can start as a root task, outside a durable execution.</summary>
public interface ISchedulableTask
{
    /// <summary>
    /// Schedules or reattaches to this definition under <paramref name="taskId"/>, and completes once the task's host has
    /// accepted and durably stored the task.
    /// </summary>
    /// <remarks>
    /// An existing identifier returns its recorded response and retains its original definition, so a caller which
    /// retries a start under the same identifier reaches the same task.
    /// </remarks>
    ValueTask<DurableTaskResponse> ScheduleAsync(TaskId taskId, CancellationToken cancellationToken);

    /// <summary>Gets a handle for a scheduled identifier.</summary>
    IScheduledTaskHandle GetHandle(TaskId taskId);

    /// <summary>
    /// Stages the start of this definition under <paramref name="taskId"/> in the calling code's durable outbox, and
    /// returns a handle for the task.
    /// </summary>
    /// <remarks>
    /// The start commits with the caller's next durable write, together with the state changes which the calling code
    /// staged, and its host then delivers it exactly once. Implementations which have no durable outbox to stage in, such
    /// as clients, keep this default implementation, which throws <see cref="InvalidOperationException"/>.
    /// </remarks>
    IScheduledTaskHandle Post(TaskId taskId)
        => throw new InvalidOperationException(
            "Post stages a start in the durable outbox of the calling grain, and this caller has none. Use ScheduleAsync, which completes once the task's host has accepted the task.");
}

/// <summary>Controls and observes one scheduled durable task.</summary>
public interface IScheduledTaskHandle
{
    /// <summary>Gets the scheduled task identifier.</summary>
    TaskId TaskId { get; }

    /// <summary>
    /// Gets the identifier under which the task's host stores the task which this handle observes; see
    /// <see cref="ScheduledTask.TargetTaskId"/>.
    /// </summary>
    TaskId TargetTaskId => TaskId;

    /// <summary>
    /// Returns the feature <typeparamref name="TFeature"/> through which the host describes the task, or
    /// <see langword="null"/>; see <see cref="ScheduledTask.GetFeature{TFeature}"/>.
    /// </summary>
    /// <typeparam name="TFeature">The feature's type.</typeparam>
    TFeature? GetFeature<TFeature>()
        where TFeature : class => null;

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
    /// Durably requests task cancellation carrying <paramref name="reason"/>. The request is monotonic and idempotent.
    /// The token abandons waiting for acknowledgement.
    /// </summary>
    /// <param name="reason">
    /// The reason which the task's <see cref="DurableTaskContext.TaskCancellationReason"/> reports, or
    /// <see langword="null"/> for none.
    /// </param>
    /// <param name="cancellationToken">A token which abandons waiting for the request to commit.</param>
    /// <remarks>
    /// The first request determines the reason, and the host retains it with the task's cancellation. Hosts which don't
    /// record reasons keep this default implementation, which requests cancellation through
    /// <see cref="CancelAsync(CancellationToken)"/> without the reason.
    /// </remarks>
    ValueTask CancelAsync(string? reason, CancellationToken cancellationToken) => CancelAsync(cancellationToken);

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
