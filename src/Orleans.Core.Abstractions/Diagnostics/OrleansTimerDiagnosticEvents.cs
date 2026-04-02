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
public record GrainTimerTickStartEvent(
    IGrainContext GrainContext,
    string? TimerName);

/// <summary>
/// Event payload for when a grain timer tick callback has completed.
/// </summary>
/// <param name="GrainContext">The grain context that owns the timer.</param>
/// <param name="TimerName">The name of the timer (may be null if not named).</param>
/// <param name="Elapsed">The time taken to execute the callback.</param>
/// <param name="Exception">The exception thrown by the callback, if any.</param>
public record GrainTimerTickStopEvent(
    IGrainContext GrainContext,
    string? TimerName,
    TimeSpan Elapsed,
    Exception? Exception);

/// <summary>
/// Event payload for when a grain timer is created.
/// </summary>
/// <param name="GrainContext">The grain context that owns the timer.</param>
/// <param name="TimerName">The name of the timer (may be null if not named).</param>
/// <param name="DueTime">The initial due time of the timer.</param>
/// <param name="Period">The period of the timer.</param>
public record GrainTimerCreatedEvent(
    IGrainContext GrainContext,
    string? TimerName,
    TimeSpan DueTime,
    TimeSpan Period);

/// <summary>
/// Event payload for when a grain timer is disposed.
/// </summary>
/// <param name="GrainContext">The grain context that owns the timer.</param>
/// <param name="TimerName">The name of the timer (may be null if not named).</param>
public record GrainTimerDisposedEvent(
    IGrainContext GrainContext,
    string? TimerName);

internal static class OrleansTimerDiagnosticListener
{
    private static readonly DiagnosticListener Listener = new(OrleansTimerDiagnostics.ListenerName);

    internal static void EmitCreated(IGrainContext grainContext, string? timerName, TimeSpan dueTime, TimeSpan period)
    {
        if (!Listener.IsEnabled(OrleansTimerDiagnostics.EventNames.Created))
        {
            return;
        }

        Emit(Listener, grainContext, timerName, dueTime, period);

        static void Emit(DiagnosticListener listener, IGrainContext grainContext, string? timerName, TimeSpan dueTime, TimeSpan period)
        {
            listener.Write(OrleansTimerDiagnostics.EventNames.Created, new GrainTimerCreatedEvent(
                grainContext,
                timerName,
                dueTime,
                period));
        }
    }

    internal static void EmitDisposed(IGrainContext grainContext, string? timerName)
    {
        if (!Listener.IsEnabled(OrleansTimerDiagnostics.EventNames.Disposed))
        {
            return;
        }

        Emit(Listener, grainContext, timerName);

        static void Emit(DiagnosticListener listener, IGrainContext grainContext, string? timerName)
        {
            listener.Write(OrleansTimerDiagnostics.EventNames.Disposed, new GrainTimerDisposedEvent(
                grainContext,
                timerName));
        }
    }

    internal static GrainTimerTickDiagnosticsContext EmitTickStart(IGrainContext grainContext, string? timerName)
    {
        if (!Listener.IsEnabled(OrleansTimerDiagnostics.EventNames.TickStart))
        {
            return default;
        }

        return Emit(Listener, grainContext, timerName);

        static GrainTimerTickDiagnosticsContext Emit(DiagnosticListener listener, IGrainContext grainContext, string? timerName)
        {
            listener.Write(OrleansTimerDiagnostics.EventNames.TickStart, new GrainTimerTickStartEvent(
                grainContext,
                timerName));

            return new(true, Stopwatch.GetTimestamp());
        }
    }

    internal static void EmitTickStop(GrainTimerTickDiagnosticsContext diagnostics, IGrainContext grainContext, string? timerName, Exception? exception = null)
    {
        if (!diagnostics.EmitStopDiagnostics)
        {
            return;
        }

        Emit(Listener, diagnostics.StartTimestamp, grainContext, timerName, exception);

        static void Emit(DiagnosticListener listener, long startTimestamp, IGrainContext grainContext, string? timerName, Exception? exception)
        {
            listener.Write(OrleansTimerDiagnostics.EventNames.TickStop, new GrainTimerTickStopEvent(
                grainContext,
                timerName,
                Stopwatch.GetElapsedTime(startTimestamp),
                exception));
        }
    }
}

internal readonly record struct GrainTimerTickDiagnosticsContext(bool EmitStopDiagnostics, long StartTimestamp);
