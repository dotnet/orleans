using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Orleans.Runtime.GrainDirectory;

namespace Orleans.Runtime.Diagnostics;

/// <summary>
/// Provides diagnostic listener events for the distributed grain directory.
/// </summary>
internal static class GrainDirectoryEvents
{
    /// <summary>
    /// The name of the diagnostic listener for grain directory events.
    /// </summary>
    public const string ListenerName = "Orleans.GrainDirectory";

    private static readonly DiagnosticListener Listener = new(ListenerName);

    /// <summary>
    /// Gets an observable sequence of all grain directory events.
    /// </summary>
    public static IObservable<GrainDirectoryEvent> AllEvents { get; } = new Observable();

    /// <summary>
    /// The base class used for grain directory diagnostic events.
    /// </summary>
    public abstract class GrainDirectoryEvent(SiloAddress observerSiloAddress)
    {
        /// <summary>
        /// The silo which observed the event.
        /// </summary>
        public readonly SiloAddress ObserverSiloAddress = observerSiloAddress;
    }

    /// <summary>
    /// Event payload for when a safety lease hold is created for a dead silo.
    /// </summary>
    public sealed class SiloLeaseHoldCreated(
        SiloAddress observerSiloAddress,
        SiloAddress deadSiloAddress,
        DateTimeOffset expiration) : GrainDirectoryEvent(observerSiloAddress)
    {
        /// <summary>
        /// The dead silo whose activations are covered by the lease hold.
        /// </summary>
        public readonly SiloAddress DeadSiloAddress = deadSiloAddress;

        /// <summary>
        /// The time when the lease hold expires.
        /// </summary>
        public readonly DateTimeOffset Expiration = expiration;
    }

    /// <summary>
    /// Event payload for when a safety lease hold is created for a directory range.
    /// </summary>
    public sealed class RangeLeaseHoldCreated(
        SiloAddress observerSiloAddress,
        RingRange range,
        DateTimeOffset expiration) : GrainDirectoryEvent(observerSiloAddress)
    {
        /// <summary>
        /// The directory range covered by the lease hold.
        /// </summary>
        public readonly RingRange Range = range;

        /// <summary>
        /// The time when the lease hold expires.
        /// </summary>
        public readonly DateTimeOffset Expiration = expiration;
    }

    /// <summary>
    /// Event payload for when a registration is delayed by a dead-silo lease hold.
    /// </summary>
    public sealed class RegistrationBlockedBySiloLease(
        SiloAddress observerSiloAddress,
        GrainId grainId,
        SiloAddress deadSiloAddress,
        DateTimeOffset expiration,
        TimeSpan retryAfter) : GrainDirectoryEvent(observerSiloAddress)
    {
        /// <summary>
        /// The grain whose registration was blocked.
        /// </summary>
        public readonly GrainId GrainId = grainId;

        /// <summary>
        /// The dead silo whose lease hold blocked registration.
        /// </summary>
        public readonly SiloAddress DeadSiloAddress = deadSiloAddress;

        /// <summary>
        /// The time when the lease hold expires.
        /// </summary>
        public readonly DateTimeOffset Expiration = expiration;

        /// <summary>
        /// The delay before registration should be retried.
        /// </summary>
        public readonly TimeSpan RetryAfter = retryAfter;
    }

    /// <summary>
    /// Event payload for when a registration is delayed by a range lease hold.
    /// </summary>
    public sealed class RegistrationBlockedByRangeLease(
        SiloAddress observerSiloAddress,
        GrainId grainId,
        RingRange range,
        DateTimeOffset expiration,
        TimeSpan retryAfter) : GrainDirectoryEvent(observerSiloAddress)
    {
        /// <summary>
        /// The grain whose registration was blocked.
        /// </summary>
        public readonly GrainId GrainId = grainId;

        /// <summary>
        /// The directory range whose lease hold blocked registration.
        /// </summary>
        public readonly RingRange Range = range;

        /// <summary>
        /// The time when the lease hold expires.
        /// </summary>
        public readonly DateTimeOffset Expiration = expiration;

        /// <summary>
        /// The delay before registration should be retried.
        /// </summary>
        public readonly TimeSpan RetryAfter = retryAfter;
    }

    /// <summary>
    /// Event payload for when a grain directory operation is delayed because a lease hold is active.
    /// </summary>
    public sealed class OperationDelayedByLeaseHold(
        SiloAddress observerSiloAddress,
        GrainId grainId,
        string operation,
        TimeSpan retryAfter) : GrainDirectoryEvent(observerSiloAddress)
    {
        /// <summary>
        /// The grain whose directory operation was delayed.
        /// </summary>
        public readonly GrainId GrainId = grainId;

        /// <summary>
        /// The delayed directory operation.
        /// </summary>
        public readonly string Operation = operation;

        /// <summary>
        /// The delay before the operation should be retried.
        /// </summary>
        public readonly TimeSpan RetryAfter = retryAfter;
    }

    internal static void EmitSiloLeaseHoldCreated(SiloAddress observerSiloAddress, SiloAddress deadSiloAddress, DateTimeOffset expiration)
    {
        if (!Listener.IsEnabled(nameof(SiloLeaseHoldCreated)))
        {
            return;
        }

        Emit(observerSiloAddress, deadSiloAddress, expiration);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(SiloAddress observerSiloAddress, SiloAddress deadSiloAddress, DateTimeOffset expiration)
        {
            Listener.Write(nameof(SiloLeaseHoldCreated), new SiloLeaseHoldCreated(
                observerSiloAddress,
                deadSiloAddress,
                expiration));
        }
    }

    internal static void EmitRangeLeaseHoldCreated(SiloAddress observerSiloAddress, RingRange range, DateTimeOffset expiration)
    {
        if (!Listener.IsEnabled(nameof(RangeLeaseHoldCreated)))
        {
            return;
        }

        Emit(observerSiloAddress, range, expiration);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(SiloAddress observerSiloAddress, RingRange range, DateTimeOffset expiration)
        {
            Listener.Write(nameof(RangeLeaseHoldCreated), new RangeLeaseHoldCreated(
                observerSiloAddress,
                range,
                expiration));
        }
    }

    internal static void EmitRegistrationBlockedBySiloLease(
        SiloAddress observerSiloAddress,
        GrainId grainId,
        SiloAddress deadSiloAddress,
        DateTimeOffset expiration,
        TimeSpan retryAfter)
    {
        if (!Listener.IsEnabled(nameof(RegistrationBlockedBySiloLease)))
        {
            return;
        }

        Emit(observerSiloAddress, grainId, deadSiloAddress, expiration, retryAfter);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(
            SiloAddress observerSiloAddress,
            GrainId grainId,
            SiloAddress deadSiloAddress,
            DateTimeOffset expiration,
            TimeSpan retryAfter)
        {
            Listener.Write(nameof(RegistrationBlockedBySiloLease), new RegistrationBlockedBySiloLease(
                observerSiloAddress,
                grainId,
                deadSiloAddress,
                expiration,
                retryAfter));
        }
    }

    internal static void EmitRegistrationBlockedByRangeLease(
        SiloAddress observerSiloAddress,
        GrainId grainId,
        RingRange range,
        DateTimeOffset expiration,
        TimeSpan retryAfter)
    {
        if (!Listener.IsEnabled(nameof(RegistrationBlockedByRangeLease)))
        {
            return;
        }

        Emit(observerSiloAddress, grainId, range, expiration, retryAfter);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(
            SiloAddress observerSiloAddress,
            GrainId grainId,
            RingRange range,
            DateTimeOffset expiration,
            TimeSpan retryAfter)
        {
            Listener.Write(nameof(RegistrationBlockedByRangeLease), new RegistrationBlockedByRangeLease(
                observerSiloAddress,
                grainId,
                range,
                expiration,
                retryAfter));
        }
    }

    internal static void EmitOperationDelayedByLeaseHold(
        SiloAddress observerSiloAddress,
        GrainId grainId,
        string operation,
        TimeSpan retryAfter)
    {
        if (!Listener.IsEnabled(nameof(OperationDelayedByLeaseHold)))
        {
            return;
        }

        Emit(observerSiloAddress, grainId, operation, retryAfter);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(
            SiloAddress observerSiloAddress,
            GrainId grainId,
            string operation,
            TimeSpan retryAfter)
        {
            Listener.Write(nameof(OperationDelayedByLeaseHold), new OperationDelayedByLeaseHold(
                observerSiloAddress,
                grainId,
                operation,
                retryAfter));
        }
    }

    private sealed class Observable : IObservable<GrainDirectoryEvent>
    {
        public IDisposable Subscribe(IObserver<GrainDirectoryEvent> observer) => Listener.Subscribe(new Observer(observer));

        private sealed class Observer(IObserver<GrainDirectoryEvent> observer) : IObserver<KeyValuePair<string, object?>>
        {
            public void OnCompleted() => observer.OnCompleted();
            public void OnError(Exception error) => observer.OnError(error);

            public void OnNext(KeyValuePair<string, object?> value)
            {
                if (value.Value is GrainDirectoryEvent evt)
                {
                    observer.OnNext(evt);
                }
            }
        }
    }
}
