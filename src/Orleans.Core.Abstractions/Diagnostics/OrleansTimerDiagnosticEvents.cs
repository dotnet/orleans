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
/// <param name="GrainContext">The grain context that owns the timer.</param>
/// <param name="TimerName">The name of the timer (may be null if not named).</param>
/// <param name="Timer">The timer instance.</param>
public class GrainTimerTickStartEvent(
    IGrainContext GrainContext,
    string? TimerName,
    IGrainTimer Timer)
{
    public IGrainContext GrainContext { get; } = GrainContext;
    public string? TimerName { get; } = TimerName;
    public IGrainTimer Timer { get; } = Timer;
}

/// <summary>
/// Event payload for when a grain timer tick callback has completed.
/// </summary>
/// <param name="GrainContext">The grain context that owns the timer.</param>
/// <param name="TimerName">The name of the timer (may be null if not named).</param>
/// <param name="Elapsed">The time taken to execute the callback.</param>
/// <param name="Exception">The exception thrown by the callback, if any.</param>
/// <param name="Timer">The timer instance.</param>
public class GrainTimerTickStopEvent(
    IGrainContext GrainContext,
    string? TimerName,
    TimeSpan Elapsed,
    Exception? Exception,
    IGrainTimer Timer)
{
    public IGrainContext GrainContext { get; } = GrainContext;
    public string? TimerName { get; } = TimerName;
    public TimeSpan Elapsed { get; } = Elapsed;
    public Exception? Exception { get; } = Exception;
    public IGrainTimer Timer { get; } = Timer;
}

/// <summary>
/// Event payload for when a grain timer is created.
/// </summary>
/// <param name="GrainContext">The grain context that owns the timer.</param>
/// <param name="TimerName">The name of the timer (may be null if not named).</param>
/// <param name="DueTime">The initial due time of the timer.</param>
/// <param name="Period">The period of the timer.</param>
/// <param name="Timer">The timer instance.</param>
public class GrainTimerCreatedEvent(
    IGrainContext GrainContext,
    string? TimerName,
    TimeSpan DueTime,
    TimeSpan Period,
    IGrainTimer Timer)
{
    public IGrainContext GrainContext { get; } = GrainContext;
    public string? TimerName { get; } = TimerName;
    public TimeSpan DueTime { get; } = DueTime;
    public TimeSpan Period { get; } = Period;
    public IGrainTimer Timer { get; } = Timer;
}

/// <summary>
/// Event payload for when a grain timer is disposed.
/// </summary>
/// <param name="GrainContext">The grain context that owns the timer.</param>
/// <param name="TimerName">The name of the timer (may be null if not named).</param>
/// <param name="Timer">The timer instance.</param>
public class GrainTimerDisposedEvent(
    IGrainContext GrainContext,
    string? TimerName,
    IGrainTimer Timer)
{
    public IGrainContext GrainContext { get; } = GrainContext;
    public string? TimerName { get; } = TimerName;
    public IGrainTimer Timer { get; } = Timer;
}

internal static class OrleansTimerDiagnosticListener
{
    private static readonly DiagnosticListener Listener = new(OrleansTimerDiagnostics.ListenerName);

    internal static void EmitCreated(IGrainContext grainContext, string? timerName, TimeSpan dueTime, TimeSpan period, IGrainTimer timer)
    {
        if (!Listener.IsEnabled(OrleansTimerDiagnostics.EventNames.Created))
        {
            return;
        }

        Emit(Listener, grainContext, timerName, dueTime, period, timer);

        static void Emit(DiagnosticListener listener, IGrainContext grainContext, string? timerName, TimeSpan dueTime, TimeSpan period, IGrainTimer timer)
        {
            listener.Write(OrleansTimerDiagnostics.EventNames.Created, new GrainTimerCreatedEvent(
                grainContext,
                timerName,
                dueTime,
                period,
                timer));
        }
    }

    internal static void EmitDisposed(IGrainContext grainContext, string? timerName, IGrainTimer timer)
    {
        if (!Listener.IsEnabled(OrleansTimerDiagnostics.EventNames.Disposed))
        {
            return;
        }

        Emit(Listener, grainContext, timerName, timer);

        static void Emit(DiagnosticListener listener, IGrainContext grainContext, string? timerName, IGrainTimer timer)
        {
            listener.Write(OrleansTimerDiagnostics.EventNames.Disposed, new GrainTimerDisposedEvent(
                grainContext,
                timerName,
                timer));
        }
    }

    internal static GrainTimerTickDiagnosticsContext EmitTickStart(IGrainContext grainContext, string? timerName, IGrainTimer timer)
    {
        if (!Listener.IsEnabled(OrleansTimerDiagnostics.EventNames.TickStart))
        {
            return default;
        }

        return Emit(Listener, grainContext, timerName, timer);

        static GrainTimerTickDiagnosticsContext Emit(DiagnosticListener listener, IGrainContext grainContext, string? timerName, IGrainTimer timer)
        {
            listener.Write(OrleansTimerDiagnostics.EventNames.TickStart, new GrainTimerTickStartEvent(
                grainContext,
                timerName,
                timer));

            return new(true, Stopwatch.GetTimestamp());
        }
    }

    internal static void EmitTickStop(GrainTimerTickDiagnosticsContext diagnostics, IGrainContext grainContext, string? timerName, IGrainTimer timer, Exception? exception = null)
    {
        if (!diagnostics.EmitStopDiagnostics)
        {
            return;
        }

        Emit(Listener, diagnostics.StartTimestamp, grainContext, timerName, timer, exception);

        static void Emit(DiagnosticListener listener, long startTimestamp, IGrainContext grainContext, string? timerName, IGrainTimer timer, Exception? exception)
        {
            listener.Write(OrleansTimerDiagnostics.EventNames.TickStop, new GrainTimerTickStopEvent(
                grainContext,
                timerName,
                Stopwatch.GetElapsedTime(startTimestamp),
                exception,
                timer));
        }
    }
}

internal readonly record struct GrainTimerTickDiagnosticsContext(bool EmitStopDiagnostics, long StartTimestamp);
