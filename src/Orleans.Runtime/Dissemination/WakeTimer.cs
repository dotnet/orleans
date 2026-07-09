namespace Orleans.Runtime.Dissemination;

/// <summary>
/// A thread-safe, wakeable, one-shot timer.
/// </summary>
/// <remarks>
/// The timer has one reusable underlying <see cref="ITimer"/>. Call <see cref="Change"/> to arm or re-arm it
/// with a due time, call <see cref="Wake"/> to complete the current or next wait immediately, and call
/// <see cref="WaitAsync"/> to wait until either the due time elapses or the timer is explicitly woken.
/// </remarks>
internal sealed class WakeTimer : IDisposable
{
    private readonly object _lock = new();
    private readonly ITimer _timer;
    private TaskCompletionSource<bool>? _waiter;
    private bool _signaled;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="WakeTimer"/> class.
    /// </summary>
    /// <param name="timeProvider">The time provider used to create the underlying timer.</param>
    public WakeTimer(TimeProvider timeProvider)
    {
        _timer = timeProvider.CreateTimer(
            static state => ((WakeTimer)state!).Wake(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Arms or re-arms the timer to fire once after the provided due time.
    /// </summary>
    /// <param name="dueTime">
    /// The delay before the timer fires. <see cref="TimeSpan.Zero"/> schedules an immediate wake, and
    /// <see cref="Timeout.InfiniteTimeSpan"/> prevents the timer from firing until changed or woken.
    /// </param>
    /// <remarks>
    /// The timer always uses an infinite period, so every call is a one-shot schedule. Calling this method while
    /// another caller is waiting causes that waiter to observe the new due time rather than completing immediately.
    /// </remarks>
    public void Change(TimeSpan dueTime)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _timer.Change(dueTime, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Immediately completes the current wait, or causes the next wait to complete immediately.
    /// </summary>
    public void Wake()
    {
        TaskCompletionSource<bool>? waiter;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            waiter = CompleteWaitUnsafe();
            if (waiter is null)
            {
                _signaled = true;
            }
        }

        waiter?.TrySetResult(true);
    }

    /// <summary>
    /// Waits until the timer fires, is explicitly woken, or is disposed.
    /// </summary>
    /// <param name="cancellationToken">A token which cancels this wait without disarming the timer.</param>
    /// <returns>
    /// <see langword="true"/> when the due time elapsed or <see cref="Wake"/> was called; <see langword="false"/>
    /// when the timer was disposed.
    /// </returns>
    /// <remarks>
    /// Only one waiter is supported at a time. If the timer has already fired or been woken before this method is
    /// called, the method returns <see langword="true"/> immediately. Cancelling a wait does not disarm the timer,
    /// so a later waiter can still observe the scheduled wakeup.
    /// </remarks>
    public async ValueTask<bool> WaitAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        TaskCompletionSource<bool> waiter;
        lock (_lock)
        {
            if (_disposed)
            {
                return false;
            }

            if (_signaled)
            {
                _signaled = false;
                return true;
            }

            if (_waiter is not null)
            {
                throw new InvalidOperationException("Only one waiter can wait on a WakeTimer at a time.");
            }

            waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiter = waiter;
        }

        using var registration = cancellationToken.UnsafeRegister(
            static state =>
            {
                var (timer, waiter, token) =
                    ((WakeTimer Timer, TaskCompletionSource<bool> Waiter, CancellationToken Token))state!;
                timer.CancelWait(waiter, token);
            },
            (this, waiter, cancellationToken));

        return await waiter.Task;
    }

    /// <summary>
    /// Disposes the timer, completing any current waiter with <see langword="false"/>.
    /// </summary>
    public void Dispose()
    {
        TaskCompletionSource<bool>? waiter;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            waiter = CompleteWaitUnsafe();
        }

        waiter?.TrySetResult(false);
        _timer.Dispose();
    }

    private TaskCompletionSource<bool>? CompleteWaitUnsafe()
    {
        var waiter = _waiter;
        _waiter = null;
        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        return waiter;
    }

    private void CancelWait(TaskCompletionSource<bool> waiter, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(_waiter, waiter))
            {
                return;
            }

            _waiter = null;
            waiter.TrySetCanceled(cancellationToken);
        }
    }
}
