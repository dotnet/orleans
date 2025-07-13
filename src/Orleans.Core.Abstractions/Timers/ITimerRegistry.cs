using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Timers;

/// <summary>
/// Functionality for managing grain timers.
/// </summary>
public interface ITimerRegistry
{
    /// <summary>
    /// Creates a grain timer.
    /// </summary>
    /// <param name="grainContext">The grain which the timer is associated with.</param>
    /// <param name="callback">The timer callback, which will fire whenever the timer becomes due.</param>
    /// <param name="state">The state object passed to the callback.</param>
    /// <param name="dueTime">
    /// The amount of time to delay before the <paramref name="callback"/> is invoked.
    /// Specify <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> to prevent the timer from starting.
    /// Specify <see cref="TimeSpan.Zero"/> to invoke the callback promptly.
    /// </param>
    /// <param name="period">
    /// The time interval between invocations of <paramref name="callback"/>.
    /// Specify <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> to disable periodic signaling.
    /// </param>
    /// <returns>
    /// An <see cref="IDisposable"/> instance which represents the timer.
    /// </returns>
    [Obsolete("Use 'RegisterGrainTimer(grainContext, callback, state, new() { DueTime = dueTime, Period = period, Interleave = true })' instead.")]
    IDisposable RegisterTimer(IGrainContext grainContext, Func<object?, Task> callback, object? state, TimeSpan dueTime, TimeSpan period);

    /// <inheritdoc cref="GrainBaseExtensions.RegisterGrainTimer{TState}(IGrainBase, Func{TState, CancellationToken, Task}, TState, GrainTimerCreationOptions)"/>
    /// <param name="grainContext">The grain which the timer is associated with.</param>
    /// <typeparam name="TState">The type of the <paramref name="state"/> parameter.</typeparam>
    IGrainTimer RegisterGrainTimer<TState>(IGrainContext grainContext, Func<TState, CancellationToken, Task> callback, TState state, GrainTimerCreationOptions options);
}
