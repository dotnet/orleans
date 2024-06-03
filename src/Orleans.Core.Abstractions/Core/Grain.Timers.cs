#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans;

public abstract partial class Grain
{
    /// <summary>
    /// Creates a grain timer.
    /// </summary>
    /// <param name="callback">The timer callback, which will be invoked whenever the timer becomes due.</param>
    /// <param name="options">
    /// The options for creating the timer.
    /// </param>
    /// <returns>
    /// The <see cref="IGrainTimer"/> instance which represents the timer.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Grain timers do not keep grains active by default. Setting <see cref="GrainTimerCreationOptions.KeepAlive"/> to
    /// <see langword="true"/> causes each timer tick to extend the grain activation's lifetime.
    /// If the timer ticks are infrequent, the grain can still be deactivated due to idleness.
    /// When a grain is deactivated, all active timers are discarded.
    /// </para>
    /// <para>
    /// Until the <see cref="Task"/> returned from the callback is resolved, the next timer tick will not be scheduled.
    /// That is to say, a timer callback will never be concurrently executed with itself.
    /// If <see cref="GrainTimerCreationOptions.Interleave"/> is set to <see langword="true"/>, the timer callback will be allowed
    /// to interleave with with other grain method calls and other timers.
    /// If <see cref="GrainTimerCreationOptions.Interleave"/> is set to <see langword="false"/>, the timer callback will respect the
    /// reentrancy setting of the grain, just like a typical grain method call.
    /// </para>
    /// <para>
    /// The timer may be stopped at any time by calling the <see cref="IGrainTimer"/>'s <see cref="IDisposable.Dispose"/> method.
    /// Disposing a timer prevents any further timer ticks from being scheduled.
    /// </para>
    /// <para>
    /// The timer due time and period can be updated by calling its <see cref="IGrainTimer.Change(TimeSpan, TimeSpan)"/> method.
    /// Each time the timer is updated, the next timer tick will be scheduled based on the updated due time.
    /// Subsequent ticks will be scheduled after the updated period elapses.
    /// Note that this behavior is the same as the <see cref="Timer.Change(TimeSpan, TimeSpan)"/> method.
    /// </para>
    /// <para>
    /// Exceptions thrown from the callback will be logged, but will not prevent the next timer tick from being queued.
    /// </para>
    /// </remarks>
    protected IGrainTimer RegisterGrainTimer(Func<CancellationToken, Task> callback, GrainTimerCreationOptions options)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return RegisterGrainTimer(static (callback, cancellationToken) => callback(cancellationToken), callback, options);
    }

    /// <inheritdoc cref="RegisterGrainTimer(Func{CancellationToken, Task}, GrainTimerCreationOptions)"/>
    /// <param name="state">The state passed to the callback.</param>
    protected internal IGrainTimer RegisterGrainTimer<TState>(Func<TState, CancellationToken, Task> callback, TState state, GrainTimerCreationOptions options)
    {
        ArgumentNullException.ThrowIfNull(callback);

        EnsureRuntime();
        return Runtime.TimerRegistry.RegisterGrainTimer(GrainContext, callback, state, options);
    }

    /// <inheritdoc cref="RegisterGrainTimer(Func{CancellationToken, Task}, GrainTimerCreationOptions)"/>
    protected IGrainTimer RegisterGrainTimer(Func<Task> callback, GrainTimerCreationOptions options)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return RegisterGrainTimer(static (callback, cancellationToken) => callback(), callback, options);
    }

    /// <inheritdoc cref="RegisterGrainTimer(Func{Task}, GrainTimerCreationOptions)"/>
    /// <param name="state">The state passed to the callback.</param>
    protected IGrainTimer RegisterGrainTimer<TState>(Func<TState, Task> callback, TState state, GrainTimerCreationOptions options)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return RegisterGrainTimer(static (state, _) => state.Callback(state.State), (Callback: callback, State: state), options);
    }

    /// <summary>
    /// Creates a grain timer.
    /// </summary>
    /// <param name="callback">The timer callback, which will be invoked whenever the timer becomes due.</param>
    /// <param name="dueTime">
    /// A <see cref="TimeSpan"/> representing the amount of time to delay before invoking the callback method specified when the <see cref="IGrainTimer"/> was constructed.
    /// Specify <see cref="Timeout.InfiniteTimeSpan"/> to prevent the timer from starting.
    /// Specify <see cref="TimeSpan.Zero"/> to start the timer immediately.
    /// </param>
    /// <param name="period">
    /// The time interval between invocations of the callback method specified when the <see cref="IGrainTimer"/> was constructed.
    /// Specify <see cref="Timeout.InfiniteTimeSpan "/> to disable periodic signaling.
    /// </param>
    /// <returns>
    /// The <see cref="IGrainTimer"/> instance which represents the timer.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Grain timers do not keep grains active by default. Setting <see cref="GrainTimerCreationOptions.KeepAlive"/> to
    /// <see langword="true"/> causes each timer tick to extend the grain activation's lifetime.
    /// If the timer ticks are infrequent, the grain can still be deactivated due to idleness.
    /// When a grain is deactivated, all active timers are discarded.
    /// </para>
    /// <para>
    /// Until the <see cref="Task"/> returned from the callback is resolved, the next timer tick will not be scheduled.
    /// That is to say, a timer callback will never be concurrently executed with itself.
    /// If <see cref="GrainTimerCreationOptions.Interleave"/> is set to <see langword="true"/>, the timer callback will be allowed
    /// to interleave with with other grain method calls and other timers.
    /// If <see cref="GrainTimerCreationOptions.Interleave"/> is set to <see langword="false"/>, the timer callback will respect the
    /// reentrancy setting of the grain, just like a typical grain method call.
    /// </para>
    /// <para>
    /// The timer may be stopped at any time by calling the <see cref="IGrainTimer"/>'s <see cref="IDisposable.Dispose"/> method.
    /// Disposing a timer prevents any further timer ticks from being scheduled.
    /// </para>
    /// <para>
    /// The timer due time and period can be updated by calling its <see cref="IGrainTimer.Change(TimeSpan, TimeSpan)"/> method.
    /// Each time the timer is updated, the next timer tick will be scheduled based on the updated due time.
    /// Subsequent ticks will be scheduled after the updated period elapses.
    /// Note that this behavior is the same as the <see cref="Timer.Change(TimeSpan, TimeSpan)"/> method.
    /// </para>
    /// <para>
    /// Exceptions thrown from the callback will be logged, but will not prevent the next timer tick from being queued.
    /// </para>
    /// </remarks>
    protected IGrainTimer RegisterGrainTimer(Func<Task> callback, TimeSpan dueTime, TimeSpan period)
        => RegisterGrainTimer(callback, new() { DueTime = dueTime, Period = period });

    /// <inheritdoc cref="RegisterGrainTimer(Func{Task}, TimeSpan, TimeSpan)"/>
    protected IGrainTimer RegisterGrainTimer(Func<CancellationToken, Task> callback, TimeSpan dueTime, TimeSpan period)
        => RegisterGrainTimer(callback, new() { DueTime = dueTime, Period = period });

    /// <inheritdoc cref="RegisterGrainTimer(Func{Task}, TimeSpan, TimeSpan)"/>
    /// <param name="state">The state passed to the callback.</param>
    protected IGrainTimer RegisterGrainTimer<TState>(Func<TState, Task> callback, TState state, TimeSpan dueTime, TimeSpan period)
        => RegisterGrainTimer(callback, state, new() { DueTime = dueTime, Period = period });

    /// <inheritdoc cref="RegisterGrainTimer(Func{Task}, TimeSpan, TimeSpan)"/>
    /// <param name="state">The state passed to the callback.</param>
    protected IGrainTimer RegisterGrainTimer<TState>(Func<TState, CancellationToken, Task> callback, TState state, TimeSpan dueTime, TimeSpan period)
        => RegisterGrainTimer(callback, state, new() { DueTime = dueTime, Period = period });
}
