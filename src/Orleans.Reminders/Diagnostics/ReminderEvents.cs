using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Orleans.Runtime;

namespace Orleans.Reminders.Diagnostics;

/// <summary>
/// Provides the diagnostic listener and event payload types for Orleans reminder events.
/// </summary>
/// <remarks>
/// These types are public but may change between minor versions. They are intended for
/// advanced scenarios such as simulation testing and diagnostics.
/// </remarks>
public static class ReminderEvents
{
    /// <summary>
    /// The name of the diagnostic listener for reminder events.
    /// </summary>
    public const string ListenerName = "Orleans.Reminders";

    private static readonly DiagnosticListener Listener = new(ListenerName);

    /// <summary>
    /// Gets an observable sequence of all reminder events.
    /// </summary>
    public static IObservable<ReminderEvent> AllEvents { get; } = new Observable();

    /// <summary>
    /// The base class used for reminder diagnostic events.
    /// </summary>
    /// <param name="grainId">The grain associated with the reminder.</param>
    /// <param name="reminderName">The reminder name.</param>
    /// <param name="siloAddress">The address of the silo associated with the event, if any.</param>
    public abstract class ReminderEvent(GrainId grainId, string reminderName, SiloAddress? siloAddress)
    {
        /// <summary>
        /// The grain associated with the reminder.
        /// </summary>
        public readonly GrainId GrainId = grainId;

        /// <summary>
        /// The reminder name.
        /// </summary>
        public readonly string ReminderName = reminderName;

        /// <summary>
        /// The address of the silo associated with the event, if any.
        /// </summary>
        public readonly SiloAddress? SiloAddress = siloAddress;
    }

    /// <summary>
    /// Event payload for when a reminder is registered or updated.
    /// </summary>
    /// <param name="grainId">The grain associated with the reminder.</param>
    /// <param name="reminderName">The reminder name.</param>
    /// <param name="siloAddress">The address of the silo handling this reminder.</param>
    public sealed class Registered(
        GrainId grainId,
        string reminderName,
        SiloAddress? siloAddress) : ReminderEvent(grainId, reminderName, siloAddress)
    {
    }

    /// <summary>
    /// Event payload for when a reminder is unregistered.
    /// </summary>
    /// <param name="grainId">The grain associated with the reminder.</param>
    /// <param name="reminderName">The reminder name.</param>
    /// <param name="siloAddress">The address of the silo that was handling this reminder.</param>
    public sealed class Unregistered(
        GrainId grainId,
        string reminderName,
        SiloAddress? siloAddress) : ReminderEvent(grainId, reminderName, siloAddress)
    {
    }

    /// <summary>
    /// The reason a local reminder timer stopped.
    /// </summary>
    public enum LocalReminderStopReason
    {
        Unknown = 0,
        Unregistered = 1,
        Replaced = 2,
        RemovedFromRange = 3,
        RemovedFromTable = 4,
        ServiceStopped = 5,
    }

    /// <summary>
    /// Event payload for when a silo starts a local timer for a reminder instance.
    /// </summary>
    /// <param name="grainId">The grain associated with the reminder.</param>
    /// <param name="reminderName">The reminder name.</param>
    /// <param name="identity">The object reference used to correlate this local reminder instance across lifecycle events.</param>
    /// <param name="siloAddress">The address of the silo handling this reminder.</param>
    public sealed class LocalReminderStarted(
        GrainId grainId,
        string reminderName,
        object identity,
        SiloAddress? siloAddress) : ReminderEvent(grainId, reminderName, siloAddress)
    {
        /// <summary>
        /// The object reference used to correlate this local reminder instance across lifecycle events.
        /// </summary>
        public readonly object Identity = identity;
    }

    /// <summary>
    /// Event payload for when a silo stops a local timer for a reminder instance.
    /// </summary>
    /// <param name="grainId">The grain associated with the reminder.</param>
    /// <param name="reminderName">The reminder name.</param>
    /// <param name="identity">The object reference used to correlate this local reminder instance across lifecycle events.</param>
    /// <param name="reason">The reason the local timer stopped.</param>
    /// <param name="siloAddress">The address of the silo handling this reminder.</param>
    public sealed class LocalReminderStopped(
        GrainId grainId,
        string reminderName,
        object identity,
        LocalReminderStopReason reason,
        SiloAddress? siloAddress) : ReminderEvent(grainId, reminderName, siloAddress)
    {
        /// <summary>
        /// The object reference used to correlate this local reminder instance across lifecycle events.
        /// </summary>
        public readonly object Identity = identity;

        /// <summary>
        /// The reason the local timer stopped.
        /// </summary>
        public readonly LocalReminderStopReason Reason = reason;
    }

    /// <summary>
    /// Event payload for when a reminder tick is about to fire.
    /// </summary>
    /// <param name="grainId">The grain associated with the reminder.</param>
    /// <param name="reminderName">The reminder name.</param>
    /// <param name="status">The tick status passed to the grain.</param>
    /// <param name="siloAddress">The address of the silo handling this reminder.</param>
    public sealed class TickFiring(
        GrainId grainId,
        string reminderName,
        TickStatus status,
        SiloAddress? siloAddress) : ReminderEvent(grainId, reminderName, siloAddress)
    {
        /// <summary>
        /// The tick status passed to the grain.
        /// </summary>
        public readonly TickStatus Status = status;
    }

    /// <summary>
    /// Event payload for when a reminder tick has completed successfully.
    /// </summary>
    /// <param name="grainId">The grain associated with the reminder.</param>
    /// <param name="reminderName">The reminder name.</param>
    /// <param name="status">The tick status passed to the grain.</param>
    /// <param name="siloAddress">The address of the silo handling this reminder.</param>
    public sealed class TickCompleted(
        GrainId grainId,
        string reminderName,
        TickStatus status,
        SiloAddress? siloAddress) : ReminderEvent(grainId, reminderName, siloAddress)
    {
        /// <summary>
        /// The tick status passed to the grain.
        /// </summary>
        public readonly TickStatus Status = status;
    }

    /// <summary>
    /// Event payload for when a reminder tick has failed.
    /// </summary>
    /// <param name="grainId">The grain associated with the reminder.</param>
    /// <param name="reminderName">The reminder name.</param>
    /// <param name="status">The tick status passed to the grain.</param>
    /// <param name="exception">The exception that caused the failure.</param>
    /// <param name="siloAddress">The address of the silo handling this reminder.</param>
    public sealed class TickFailed(
        GrainId grainId,
        string reminderName,
        TickStatus status,
        Exception exception,
        SiloAddress? siloAddress) : ReminderEvent(grainId, reminderName, siloAddress)
    {
        /// <summary>
        /// The tick status passed to the grain.
        /// </summary>
        public readonly TickStatus Status = status;

        /// <summary>
        /// The exception that caused the failure.
        /// </summary>
        public readonly Exception Exception = exception;
    }

    internal static void EmitRegistered(GrainId grainId, string reminderName, SiloAddress? siloAddress)
    {
        if (!Listener.IsEnabled(nameof(Registered)))
        {
            return;
        }

        Emit(grainId, reminderName, siloAddress);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(GrainId grainId, string reminderName, SiloAddress? siloAddress)
        {
            Listener.Write(nameof(Registered), new Registered(
                grainId,
                reminderName,
                siloAddress));
        }
    }

    internal static void EmitUnregistered(GrainId grainId, string reminderName, SiloAddress? siloAddress)
    {
        if (!Listener.IsEnabled(nameof(Unregistered)))
        {
            return;
        }

        Emit(grainId, reminderName, siloAddress);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(GrainId grainId, string reminderName, SiloAddress? siloAddress)
        {
            Listener.Write(nameof(Unregistered), new Unregistered(
                grainId,
                reminderName,
                siloAddress));
        }
    }

    internal static void EmitLocalReminderStarted(GrainId grainId, string reminderName, object identity, SiloAddress? siloAddress)
    {
        ArgumentNullException.ThrowIfNull(identity);

        if (!Listener.IsEnabled(nameof(LocalReminderStarted)))
        {
            return;
        }

        Emit(grainId, reminderName, identity, siloAddress);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(GrainId grainId, string reminderName, object identity, SiloAddress? siloAddress)
        {
            Listener.Write(nameof(LocalReminderStarted), new LocalReminderStarted(
                grainId,
                reminderName,
                identity,
                siloAddress));
        }
    }

    internal static void EmitLocalReminderStopped(GrainId grainId, string reminderName, object identity, LocalReminderStopReason reason, SiloAddress? siloAddress)
    {
        ArgumentNullException.ThrowIfNull(identity);

        if (!Listener.IsEnabled(nameof(LocalReminderStopped)))
        {
            return;
        }

        Emit(grainId, reminderName, identity, reason, siloAddress);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(GrainId grainId, string reminderName, object identity, LocalReminderStopReason reason, SiloAddress? siloAddress)
        {
            Listener.Write(nameof(LocalReminderStopped), new LocalReminderStopped(
                grainId,
                reminderName,
                identity,
                reason,
                siloAddress));
        }
    }

    internal static void EmitTickFiring(GrainId grainId, string reminderName, TickStatus status, SiloAddress? siloAddress)
    {
        if (!Listener.IsEnabled(nameof(TickFiring)))
        {
            return;
        }

        Emit(grainId, reminderName, status, siloAddress);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(GrainId grainId, string reminderName, TickStatus status, SiloAddress? siloAddress)
        {
            Listener.Write(nameof(TickFiring), new TickFiring(
                grainId,
                reminderName,
                status,
                siloAddress));
        }
    }

    internal static void EmitTickCompleted(GrainId grainId, string reminderName, TickStatus status, SiloAddress? siloAddress)
    {
        if (!Listener.IsEnabled(nameof(TickCompleted)))
        {
            return;
        }

        Emit(grainId, reminderName, status, siloAddress);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(GrainId grainId, string reminderName, TickStatus status, SiloAddress? siloAddress)
        {
            Listener.Write(nameof(TickCompleted), new TickCompleted(
                grainId,
                reminderName,
                status,
                siloAddress));
        }
    }

    internal static void EmitTickFailed(GrainId grainId, string reminderName, TickStatus status, Exception exception, SiloAddress? siloAddress)
    {
        if (!Listener.IsEnabled(nameof(TickFailed)))
        {
            return;
        }

        Emit(grainId, reminderName, status, exception, siloAddress);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(GrainId grainId, string reminderName, TickStatus status, Exception exception, SiloAddress? siloAddress)
        {
            Listener.Write(nameof(TickFailed), new TickFailed(
                grainId,
                reminderName,
                status,
                exception,
                siloAddress));
        }
    }

    private sealed class Observable : IObservable<ReminderEvent>
    {
        public IDisposable Subscribe(IObserver<ReminderEvent> observer) => Listener.Subscribe(new Observer(observer));

        private sealed class Observer(IObserver<ReminderEvent> observer) : IObserver<KeyValuePair<string, object?>>
        {
            public void OnCompleted() => observer.OnCompleted();
            public void OnError(Exception error) => observer.OnError(error);

            public void OnNext(KeyValuePair<string, object?> value)
            {
                if (value.Value is ReminderEvent evt)
                {
                    observer.OnNext(evt);
                }
            }
        }
    }
}
