using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Orleans.Concurrency;

namespace Orleans.Runtime;

/// <summary>
/// Options for creating grain timers.
/// </summary>
public readonly struct GrainTimerCreationOptions()
{
    /// <summary>
    /// Initializes a new <see cref="GrainTimerCreationOptions"/> instance.
    /// </summary>
    /// <param name="dueTime">
    /// A <see cref="TimeSpan"/> representing the amount of time to delay before invoking the callback method specified when the <see cref="IGrainTimer"/> was constructed.
    /// Specify <see cref="Timeout.InfiniteTimeSpan"/> to prevent the timer from starting.
    /// Specify <see cref="TimeSpan.Zero"/> to start the timer immediately.
    /// </param>
    /// <param name="period">
    /// The time interval between invocations of the callback method specified when the <see cref="IGrainTimer"/> was constructed.
    /// Specify <see cref="Timeout.InfiniteTimeSpan "/> to disable periodic signaling.
    /// </param>
    [SetsRequiredMembers]
    public GrainTimerCreationOptions(TimeSpan dueTime, TimeSpan period) : this()
    {
        DueTime = dueTime;
        Period = period;
    }

    /// <summary>
    /// A <see cref="TimeSpan"/> representing the amount of time to delay before invoking the callback method specified when the <see cref="IGrainTimer"/> was constructed.
    /// Specify <see cref="Timeout.InfiniteTimeSpan"/> to prevent the timer from starting.
    /// Specify <see cref="TimeSpan.Zero"/> to start the timer immediately.
    /// </summary>
    public required TimeSpan DueTime { get; init; }

    /// <summary>
    /// The time interval between invocations of the callback method specified when the <see cref="IGrainTimer"/> was constructed.
    /// Specify <see cref="Timeout.InfiniteTimeSpan "/> to disable periodic signaling.
    /// </summary>
    public required TimeSpan Period { get; init; }

    /// <summary>
    /// Gets a value indicating whether callbacks scheduled by this timer are allowed to interleave execution with other timers and grain calls.
    /// Defaults to <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// If this value is <see langword="false"/>, the timer callback will be treated akin to a grain call. If the grain scheduling this timer is reentrant
    /// (i.e., it has the <see cref="ReentrantAttribute"/> attributed applied to its implementation class), the timer callback will be allowed
    /// to interleave with other grain calls and timers regardless of the value of this property.
    /// If this value is <see langword="true"/>, the timer callback will be allowed to interleave with other timers and grain calls.
    /// </remarks>
    public bool Interleave { get; init; }

    /// <summary>
    /// Gets a value indicating whether callbacks scheduled by this timer should extend the lifetime of the grain activation.
    /// Defaults to <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// If this value is <see langword="false"/>, timer callbacks will not extend a grain activation's lifetime.
    /// If a grain is only processing this timer's callbacks and no other messages, the grain will be collected after its idle collection period expires.
    /// If this value is <see langword="true"/>, timer callback will extend a grain activation's lifetime.
    /// If the timer period is shorter than the grain's idle collection period, the grain will not be collected due to idleness.
    /// </remarks>
    public bool KeepAlive { get; init; }
}
