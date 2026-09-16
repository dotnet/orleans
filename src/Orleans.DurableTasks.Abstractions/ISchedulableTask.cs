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
    ValueTask CancelAsync(CancellationToken cancellationToken);
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
