#nullable enable
using System;
using System.Diagnostics;
using Orleans;
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
/// <param name="entry">The reminder entry.</param>
/// <param name="siloAddress">The address of the silo handling this reminder.</param>
public class ReminderRegisteredEvent(
    ReminderEntry entry,
    SiloAddress? siloAddress)
{
    public ReminderEntry Entry { get; } = entry;
    public SiloAddress? SiloAddress { get; } = siloAddress;
}

/// <summary>
/// Event payload for when a reminder is unregistered.
/// </summary>
/// <param name="reminder">The reminder handle.</param>
/// <param name="siloAddress">The address of the silo that was handling this reminder.</param>
public class ReminderUnregisteredEvent(
    IGrainReminder reminder,
    SiloAddress? siloAddress)
{
    public IGrainReminder Reminder { get; } = reminder;
    public SiloAddress? SiloAddress { get; } = siloAddress;
}

/// <summary>
/// Event payload for when a reminder tick is about to fire.
/// </summary>
/// <param name="reminder">The reminder handle.</param>
/// <param name="status">The tick status passed to the grain.</param>
/// <param name="siloAddress">The address of the silo handling this reminder.</param>
/// <param name="remindable">The reminder target grain reference.</param>
public class ReminderTickFiringEvent(
    IGrainReminder reminder,
    TickStatus status,
    SiloAddress? siloAddress,
    IRemindable remindable)
{
    public IGrainReminder Reminder { get; } = reminder;
    public TickStatus Status { get; } = status;
    public SiloAddress? SiloAddress { get; } = siloAddress;
    public IRemindable Remindable { get; } = remindable;
}

/// <summary>
/// Event payload for when a reminder tick has completed successfully.
/// </summary>
/// <param name="reminder">The reminder handle.</param>
/// <param name="status">The tick status passed to the grain.</param>
/// <param name="siloAddress">The address of the silo handling this reminder.</param>
/// <param name="remindable">The reminder target grain reference.</param>
public class ReminderTickCompletedEvent(
    IGrainReminder reminder,
    TickStatus status,
    SiloAddress? siloAddress,
    IRemindable remindable)
{
    public IGrainReminder Reminder { get; } = reminder;
    public TickStatus Status { get; } = status;
    public SiloAddress? SiloAddress { get; } = siloAddress;
    public IRemindable Remindable { get; } = remindable;
}

/// <summary>
/// Event payload for when a reminder tick has failed.
/// </summary>
/// <param name="reminder">The reminder handle.</param>
/// <param name="status">The tick status passed to the grain.</param>
/// <param name="exception">The exception that caused the failure.</param>
/// <param name="siloAddress">The address of the silo handling this reminder.</param>
/// <param name="remindable">The reminder target grain reference.</param>
public class ReminderTickFailedEvent(
    IGrainReminder reminder,
    TickStatus status,
    Exception exception,
    SiloAddress? siloAddress,
    IRemindable remindable)
{
    public IGrainReminder Reminder { get; } = reminder;
    public TickStatus Status { get; } = status;
    public Exception Exception { get; } = exception;
    public SiloAddress? SiloAddress { get; } = siloAddress;
    public IRemindable Remindable { get; } = remindable;
}

internal static class OrleansRemindersDiagnosticListener
{
    private static readonly DiagnosticListener Listener = new(OrleansRemindersDiagnostics.ListenerName);

    internal static void EmitRegistered(ReminderEntry reminder, SiloAddress? siloAddress)
    {
        if (!Listener.IsEnabled(OrleansRemindersDiagnostics.EventNames.Registered))
        {
            return;
        }

        Emit(Listener, reminder, siloAddress);

        static void Emit(DiagnosticListener listener, ReminderEntry reminder, SiloAddress? siloAddress)
        {
            listener.Write(OrleansRemindersDiagnostics.EventNames.Registered, new ReminderRegisteredEvent(
                reminder,
                siloAddress));
        }
    }

    internal static void EmitUnregistered(IGrainReminder reminder, SiloAddress? siloAddress)
    {
        if (!Listener.IsEnabled(OrleansRemindersDiagnostics.EventNames.Unregistered))
        {
            return;
        }

        Emit(Listener, reminder, siloAddress);

        static void Emit(DiagnosticListener listener, IGrainReminder reminder, SiloAddress? siloAddress)
        {
            listener.Write(OrleansRemindersDiagnostics.EventNames.Unregistered, new ReminderUnregisteredEvent(
                reminder,
                siloAddress));
        }
    }

    internal static void EmitTickFiring(IGrainReminder reminder, TickStatus status, SiloAddress? siloAddress, IRemindable remindable)
    {
        if (!Listener.IsEnabled(OrleansRemindersDiagnostics.EventNames.TickFiring))
        {
            return;
        }

        Emit(Listener, reminder, status, siloAddress, remindable);

        static void Emit(DiagnosticListener listener, IGrainReminder reminder, TickStatus status, SiloAddress? siloAddress, IRemindable remindable)
        {
            listener.Write(OrleansRemindersDiagnostics.EventNames.TickFiring, new ReminderTickFiringEvent(
                reminder,
                status,
                siloAddress,
                remindable));
        }
    }

    internal static void EmitTickCompleted(IGrainReminder reminder, TickStatus status, SiloAddress? siloAddress, IRemindable remindable)
    {
        if (!Listener.IsEnabled(OrleansRemindersDiagnostics.EventNames.TickCompleted))
        {
            return;
        }

        Emit(Listener, reminder, status, siloAddress, remindable);

        static void Emit(DiagnosticListener listener, IGrainReminder reminder, TickStatus status, SiloAddress? siloAddress, IRemindable remindable)
        {
            listener.Write(OrleansRemindersDiagnostics.EventNames.TickCompleted, new ReminderTickCompletedEvent(
                reminder,
                status,
                siloAddress,
                remindable));
        }
    }

    internal static void EmitTickFailed(IGrainReminder reminder, TickStatus status, Exception exception, SiloAddress? siloAddress, IRemindable remindable)
    {
        if (!Listener.IsEnabled(OrleansRemindersDiagnostics.EventNames.TickFailed))
        {
            return;
        }

        Emit(Listener, reminder, status, exception, siloAddress, remindable);

        static void Emit(DiagnosticListener listener, IGrainReminder reminder, TickStatus status, Exception exception, SiloAddress? siloAddress, IRemindable remindable)
        {
            listener.Write(OrleansRemindersDiagnostics.EventNames.TickFailed, new ReminderTickFailedEvent(
                reminder,
                status,
                exception,
                siloAddress,
                remindable));
        }
    }
}
