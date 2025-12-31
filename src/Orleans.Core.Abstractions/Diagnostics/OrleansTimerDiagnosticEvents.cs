#nullable enable
using System;
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
/// <param name="GrainId">The grain ID that owns the timer.</param>
/// <param name="GrainType">The grain type name.</param>
/// <param name="TimerName">The name of the timer (may be null if not named).</param>
public record GrainTimerTickStartEvent(
    GrainId GrainId,
    string GrainType,
    string? TimerName);

/// <summary>
/// Event payload for when a grain timer tick callback has completed.
/// </summary>
/// <param name="GrainId">The grain ID that owns the timer.</param>
/// <param name="GrainType">The grain type name.</param>
/// <param name="TimerName">The name of the timer (may be null if not named).</param>
/// <param name="Elapsed">The time taken to execute the callback.</param>
/// <param name="Exception">The exception thrown by the callback, if any.</param>
public record GrainTimerTickStopEvent(
    GrainId GrainId,
    string GrainType,
    string? TimerName,
    TimeSpan Elapsed,
    Exception? Exception);

/// <summary>
/// Event payload for when a grain timer is created.
/// </summary>
/// <param name="GrainId">The grain ID that owns the timer.</param>
/// <param name="GrainType">The grain type name.</param>
/// <param name="TimerName">The name of the timer (may be null if not named).</param>
/// <param name="DueTime">The initial due time of the timer.</param>
/// <param name="Period">The period of the timer.</param>
public record GrainTimerCreatedEvent(
    GrainId GrainId,
    string GrainType,
    string? TimerName,
    TimeSpan DueTime,
    TimeSpan Period);

/// <summary>
/// Event payload for when a grain timer is disposed.
/// </summary>
/// <param name="GrainId">The grain ID that owns the timer.</param>
/// <param name="GrainType">The grain type name.</param>
/// <param name="TimerName">The name of the timer (may be null if not named).</param>
public record GrainTimerDisposedEvent(
    GrainId GrainId,
    string GrainType,
    string? TimerName);
