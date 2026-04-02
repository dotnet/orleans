using System;
using System.Diagnostics;
using Orleans.Runtime;

namespace Orleans.Diagnostics;

/// <summary>
/// Provides diagnostic listener names and event names for Orleans grain timer events.
/// </summary>
/// <remarks>
/// These types are public but may change between minor versions. They are intended for
/// advanced scenarios such as simulation testing and diagnostics.
/// </remarks>
public static class OrleansTimerDiagnostics
{
    /// <summary>
    /// The name of the diagnostic listener for grain timer events.
    /// </summary>
    public const string ListenerName = "Orleans.Timers";

    /// <summary>
    /// Event names for timer diagnostics.
    /// </summary>
    public static class EventNames
    {
        /// <summary>
        /// Event fired when a grain timer tick callback is about to start.
        /// Payload: <see cref="GrainTimerTickStartEvent"/>
        /// </summary>
        public const string TickStart = "TickStart";

        /// <summary>
        /// Event fired when a grain timer tick callback has completed.
        /// Payload: <see cref="GrainTimerTickStopEvent"/>
        /// </summary>
        public const string TickStop = "TickStop";

        /// <summary>
        /// Event fired when a grain timer is created.
        /// Payload: <see cref="GrainTimerCreatedEvent"/>
        /// </summary>
        public const string Created = "Created";

        /// <summary>
        /// Event fired when a grain timer is disposed.
        /// Payload: <see cref="GrainTimerDisposedEvent"/>
        /// </summary>
        public const string Disposed = "Disposed";
    }
}

/// <summary>
/// Event payload for when a grain timer tick callback is about to start.
/// </summary>
/// <param name="grainContext">The grain context that owns the timer.</param>
/// <param name="timer">The timer instance.</param>
public class GrainTimerTickStartEvent(
    IGrainContext grainContext,
    IGrainTimer timer)
{
    public IGrainContext GrainContext { get; } = grainContext;
    public IGrainTimer Timer { get; } = timer;
}

/// <summary>
/// Event payload for when a grain timer tick callback has completed.
/// </summary>
/// <param name="grainContext">The grain context that owns the timer.</param>
/// <param name="exception">The exception thrown by the callback, if any.</param>
/// <param name="timer">The timer instance.</param>
public class GrainTimerTickStopEvent(
    IGrainContext grainContext,
    Exception? exception,
    IGrainTimer timer)
{
    public IGrainContext GrainContext { get; } = grainContext;
    public Exception? Exception { get; } = exception;
    public IGrainTimer Timer { get; } = timer;
}

/// <summary>
/// Event payload for when a grain timer is created.
/// </summary>
/// <param name="grainContext">The grain context that owns the timer.</param>
/// <param name="dueTime">The initial due time of the timer.</param>
/// <param name="period">The period of the timer.</param>
/// <param name="timer">The timer instance.</param>
public class GrainTimerCreatedEvent(
    IGrainContext grainContext,
    TimeSpan dueTime,
    TimeSpan period,
    IGrainTimer timer)
{
    public IGrainContext GrainContext { get; } = grainContext;
    public TimeSpan DueTime { get; } = dueTime;
    public TimeSpan Period { get; } = period;
    public IGrainTimer Timer { get; } = timer;
}

/// <summary>
/// Event payload for when a grain timer is disposed.
/// </summary>
/// <param name="grainContext">The grain context that owns the timer.</param>
/// <param name="timer">The timer instance.</param>
public class GrainTimerDisposedEvent(
    IGrainContext grainContext,
    IGrainTimer timer)
{
    public IGrainContext GrainContext { get; } = grainContext;
    public IGrainTimer Timer { get; } = timer;
}

internal static class OrleansTimerDiagnosticListener
{
    private static readonly DiagnosticListener Listener = new(OrleansTimerDiagnostics.ListenerName);

    internal static void EmitCreated(IGrainContext grainContext, TimeSpan dueTime, TimeSpan period, IGrainTimer timer)
    {
        if (!Listener.IsEnabled(OrleansTimerDiagnostics.EventNames.Created))
        {
            return;
        }

        Emit(Listener, grainContext, dueTime, period, timer);

        static void Emit(DiagnosticListener listener, IGrainContext grainContext, TimeSpan dueTime, TimeSpan period, IGrainTimer timer)
        {
            listener.Write(OrleansTimerDiagnostics.EventNames.Created, new GrainTimerCreatedEvent(
                grainContext,
                dueTime,
                period,
                timer));
        }
    }

    internal static void EmitDisposed(IGrainContext grainContext, IGrainTimer timer)
    {
        if (!Listener.IsEnabled(OrleansTimerDiagnostics.EventNames.Disposed))
        {
            return;
        }

        Emit(Listener, grainContext, timer);

        static void Emit(DiagnosticListener listener, IGrainContext grainContext, IGrainTimer timer)
        {
            listener.Write(OrleansTimerDiagnostics.EventNames.Disposed, new GrainTimerDisposedEvent(
                grainContext,
                timer));
        }
    }

    internal static void EmitTickStart(IGrainContext grainContext, IGrainTimer timer)
    {
        if (!Listener.IsEnabled(OrleansTimerDiagnostics.EventNames.TickStart))
        {
            return;
        }

        Emit(Listener, grainContext, timer);

        static void Emit(DiagnosticListener listener, IGrainContext grainContext, IGrainTimer timer)
        {
            listener.Write(OrleansTimerDiagnostics.EventNames.TickStart, new GrainTimerTickStartEvent(
                grainContext,
                timer));
        }
    }

    internal static void EmitTickStop(IGrainContext grainContext, IGrainTimer timer, Exception? exception = null)
    {
        if (!Listener.IsEnabled(OrleansTimerDiagnostics.EventNames.TickStop))
        {
            return;
        }

        Emit(Listener, grainContext, timer, exception);

        static void Emit(DiagnosticListener listener, IGrainContext grainContext, IGrainTimer timer, Exception? exception)
        {
            listener.Write(OrleansTimerDiagnostics.EventNames.TickStop, new GrainTimerTickStopEvent(
                grainContext,
                exception,
                timer));
        }
    }
}
