using System;
using System.Diagnostics;
using Orleans.Runtime;

namespace Orleans.Diagnostics;

/// <summary>
/// Provides diagnostic listener names and event names for Orleans reminder events.
/// </summary>
/// <remarks>
/// These types are public but may change between minor versions. They are intended for
/// advanced scenarios such as simulation testing and diagnostics.
/// </remarks>
public static class OrleansRemindersDiagnostics
{
    /// <summary>
    /// The name of the diagnostic listener for reminder events.
    /// </summary>
    public const string ListenerName = "Orleans.Reminders";

    /// <summary>
    /// Event names for reminder diagnostics.
    /// </summary>
    public static class EventNames
    {
        /// <summary>
        /// Event fired when a reminder is registered or updated.
        /// Payload: <see cref="ReminderRegisteredEvent"/>
        /// </summary>
        public const string Registered = "Registered";

        /// <summary>
        /// Event fired when a reminder is unregistered.
        /// Payload: <see cref="ReminderUnregisteredEvent"/>
        /// </summary>
        public const string Unregistered = "Unregistered";

        /// <summary>
        /// Event fired when a reminder tick is about to fire.
        /// Payload: <see cref="ReminderTickFiringEvent"/>
        /// </summary>
        public const string TickFiring = "TickFiring";

        /// <summary>
        /// Event fired when a reminder tick has completed successfully.
        /// Payload: <see cref="ReminderTickCompletedEvent"/>
        /// </summary>
        public const string TickCompleted = "TickCompleted";

        /// <summary>
        /// Event fired when a reminder tick has failed.
        /// Payload: <see cref="ReminderTickFailedEvent"/>
        /// </summary>
        public const string TickFailed = "TickFailed";
    }
}

/// <summary>
/// Event payload for when a reminder is registered or updated.
/// </summary>
/// <param name="GrainId">The grain ID that the reminder is registered for.</param>
/// <param name="ReminderName">The name of the reminder.</param>
/// <param name="DueTime">The time until the first tick.</param>
/// <param name="Period">The period between ticks.</param>
/// <param name="SiloAddress">The address of the silo handling this reminder.</param>
public record ReminderRegisteredEvent(
    GrainId GrainId,
    string ReminderName,
    TimeSpan DueTime,
    TimeSpan Period,
    SiloAddress? SiloAddress);

/// <summary>
/// Event payload for when a reminder is unregistered.
/// </summary>
/// <param name="GrainId">The grain ID that the reminder was registered for.</param>
/// <param name="ReminderName">The name of the reminder.</param>
/// <param name="SiloAddress">The address of the silo that was handling this reminder.</param>
public record ReminderUnregisteredEvent(
    GrainId GrainId,
    string ReminderName,
    SiloAddress? SiloAddress);

/// <summary>
/// Event payload for when a reminder tick is about to fire.
/// </summary>
/// <param name="GrainId">The grain ID that the reminder is registered for.</param>
/// <param name="ReminderName">The name of the reminder.</param>
/// <param name="TickTime">The time at which the tick was triggered.</param>
/// <param name="SiloAddress">The address of the silo handling this reminder.</param>
public record ReminderTickFiringEvent(
    GrainId GrainId,
    string ReminderName,
    DateTime TickTime,
    SiloAddress? SiloAddress);

/// <summary>
/// Event payload for when a reminder tick has completed successfully.
/// </summary>
/// <param name="GrainId">The grain ID that the reminder is registered for.</param>
/// <param name="ReminderName">The name of the reminder.</param>
/// <param name="Elapsed">The time taken to complete the tick.</param>
/// <param name="SiloAddress">The address of the silo handling this reminder.</param>
public record ReminderTickCompletedEvent(
    GrainId GrainId,
    string ReminderName,
    TimeSpan Elapsed,
    SiloAddress? SiloAddress);

/// <summary>
/// Event payload for when a reminder tick has failed.
/// </summary>
/// <param name="GrainId">The grain ID that the reminder is registered for.</param>
/// <param name="ReminderName">The name of the reminder.</param>
/// <param name="Exception">The exception that caused the failure.</param>
/// <param name="Elapsed">The time elapsed before the failure.</param>
/// <param name="SiloAddress">The address of the silo handling this reminder.</param>
public record ReminderTickFailedEvent(
    GrainId GrainId,
    string ReminderName,
    Exception Exception,
    TimeSpan Elapsed,
    SiloAddress? SiloAddress);

internal static class OrleansRemindersDiagnosticListener
{
    private static readonly DiagnosticListener Listener = new(OrleansRemindersDiagnostics.ListenerName);

    internal static void EmitRegistered(GrainId grainId, string reminderName, TimeSpan dueTime, TimeSpan period, SiloAddress? siloAddress)
    {
        if (!Listener.IsEnabled(OrleansRemindersDiagnostics.EventNames.Registered))
        {
            return;
        }

        Emit(Listener, grainId, reminderName, dueTime, period, siloAddress);

        static void Emit(DiagnosticListener listener, GrainId grainId, string reminderName, TimeSpan dueTime, TimeSpan period, SiloAddress? siloAddress)
        {
            listener.Write(OrleansRemindersDiagnostics.EventNames.Registered, new ReminderRegisteredEvent(
                grainId,
                reminderName,
                dueTime,
                period,
                siloAddress));
        }
    }

    internal static void EmitUnregistered(GrainId grainId, string reminderName, SiloAddress? siloAddress)
    {
        if (!Listener.IsEnabled(OrleansRemindersDiagnostics.EventNames.Unregistered))
        {
            return;
        }

        Emit(Listener, grainId, reminderName, siloAddress);

        static void Emit(DiagnosticListener listener, GrainId grainId, string reminderName, SiloAddress? siloAddress)
        {
            listener.Write(OrleansRemindersDiagnostics.EventNames.Unregistered, new ReminderUnregisteredEvent(
                grainId,
                reminderName,
                siloAddress));
        }
    }

    internal static void EmitTickCompleted(GrainId grainId, string reminderName, TimeSpan elapsed, SiloAddress? siloAddress)
    {
        if (!Listener.IsEnabled(OrleansRemindersDiagnostics.EventNames.TickCompleted))
        {
            return;
        }

        Emit(Listener, grainId, reminderName, elapsed, siloAddress);

        static void Emit(DiagnosticListener listener, GrainId grainId, string reminderName, TimeSpan elapsed, SiloAddress? siloAddress)
        {
            listener.Write(OrleansRemindersDiagnostics.EventNames.TickCompleted, new ReminderTickCompletedEvent(
                grainId,
                reminderName,
                elapsed,
                siloAddress));
        }
    }

    internal static void EmitTickFailed(GrainId grainId, string reminderName, Exception exception, TimeSpan elapsed, SiloAddress? siloAddress)
    {
        if (!Listener.IsEnabled(OrleansRemindersDiagnostics.EventNames.TickFailed))
        {
            return;
        }

        Emit(Listener, grainId, reminderName, exception, elapsed, siloAddress);

        static void Emit(DiagnosticListener listener, GrainId grainId, string reminderName, Exception exception, TimeSpan elapsed, SiloAddress? siloAddress)
        {
            listener.Write(OrleansRemindersDiagnostics.EventNames.TickFailed, new ReminderTickFailedEvent(
                grainId,
                reminderName,
                exception,
                elapsed,
                siloAddress));
        }
    }

    internal static void EmitTickFiring(GrainId grainId, string reminderName, DateTime tickTime, SiloAddress? siloAddress)
    {
        if (!Listener.IsEnabled(OrleansRemindersDiagnostics.EventNames.TickFiring))
        {
            return;
        }

        Emit(Listener, grainId, reminderName, tickTime, siloAddress);

        static void Emit(DiagnosticListener listener, GrainId grainId, string reminderName, DateTime tickTime, SiloAddress? siloAddress)
        {
            listener.Write(OrleansRemindersDiagnostics.EventNames.TickFiring, new ReminderTickFiringEvent(
                grainId,
                reminderName,
                tickTime,
                siloAddress));
        }
    }
}
