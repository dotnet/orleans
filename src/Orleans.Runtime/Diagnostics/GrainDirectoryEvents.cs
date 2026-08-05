using System.Diagnostics;
using System.Runtime.CompilerServices;
using Orleans.Runtime.GrainDirectory;

namespace Orleans.Runtime.Diagnostics;

internal static class GrainDirectoryEvents
{
    internal const string ListenerName = "Orleans.GrainDirectory";
    internal const string AcquireOperationName = "acquire";
    internal const string ReleaseOperationName = "release";

    private static readonly DiagnosticListener Listener = new(ListenerName);

    internal static IObservable<GrainDirectoryEvent> AllEvents { get; } = new Observable();

    internal abstract class GrainDirectoryEvent(
        SiloAddress siloAddress,
        int partitionIndex,
        MembershipVersion version,
        RingRange range)
    {
        public readonly SiloAddress SiloAddress = siloAddress;
        public readonly SiloAddress ObserverSiloAddress = siloAddress;
        public readonly int PartitionIndex = partitionIndex;
        public readonly MembershipVersion Version = version;
        public readonly RingRange Range = range;
    }

    internal abstract class RangeOperationEvent(
        SiloAddress siloAddress,
        int partitionIndex,
        MembershipVersion version,
        RingRange range,
        string operationName) : GrainDirectoryEvent(siloAddress, partitionIndex, version, range)
    {
        public readonly string OperationName = operationName;
    }

    internal sealed class RangeOperationStarted(
        SiloAddress siloAddress,
        int partitionIndex,
        MembershipVersion version,
        RingRange range,
        string operationName) : RangeOperationEvent(siloAddress, partitionIndex, version, range, operationName);

    internal sealed class RangeOperationCompleted(
        SiloAddress siloAddress,
        int partitionIndex,
        MembershipVersion version,
        RingRange range,
        string operationName,
        TimeSpan heldDuration,
        bool canceled) : RangeOperationEvent(siloAddress, partitionIndex, version, range, operationName)
    {
        public readonly TimeSpan HeldDuration = heldDuration;
        public readonly bool Canceled = canceled;
    }

    internal sealed class MembershipVersionApplied(
        SiloAddress siloAddress,
        MembershipVersion version) : GrainDirectoryEvent(siloAddress, partitionIndex: -1, version, RingRange.Empty);

    internal sealed class MembershipVersionObserved(
        SiloAddress siloAddress,
        int partitionIndex,
        MembershipVersion version) : GrainDirectoryEvent(siloAddress, partitionIndex, version, RingRange.Empty);

    internal static void EmitMembershipVersionApplied(
        SiloAddress siloAddress,
        MembershipVersion version)
    {
        if (!Listener.IsEnabled(nameof(MembershipVersionApplied)))
        {
            return;
        }

        Emit(siloAddress, version);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(SiloAddress siloAddress, MembershipVersion version)
        {
            Listener.Write(nameof(MembershipVersionApplied), new MembershipVersionApplied(siloAddress, version));
        }
    }

    internal static void EmitMembershipVersionObserved(
        SiloAddress siloAddress,
        int partitionIndex,
        MembershipVersion version)
    {
        if (!Listener.IsEnabled(nameof(MembershipVersionObserved)))
        {
            return;
        }

        Emit(siloAddress, partitionIndex, version);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(SiloAddress siloAddress, int partitionIndex, MembershipVersion version)
        {
            Listener.Write(nameof(MembershipVersionObserved), new MembershipVersionObserved(siloAddress, partitionIndex, version));
        }
    }

    internal static void EmitRangeOperationStarted(
        SiloAddress siloAddress,
        int partitionIndex,
        MembershipVersion version,
        RingRange range,
        string operationName)
    {
        if (!Listener.IsEnabled(nameof(RangeOperationStarted)))
        {
            return;
        }

        Emit(siloAddress, partitionIndex, version, range, operationName);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(SiloAddress siloAddress, int partitionIndex, MembershipVersion version, RingRange range, string operationName)
        {
            Listener.Write(nameof(RangeOperationStarted), new RangeOperationStarted(siloAddress, partitionIndex, version, range, operationName));
        }
    }

    internal static void EmitRangeOperationCompleted(
        SiloAddress siloAddress,
        int partitionIndex,
        MembershipVersion version,
        RingRange range,
        string operationName,
        TimeSpan heldDuration,
        bool canceled)
    {
        if (!Listener.IsEnabled(nameof(RangeOperationCompleted)))
        {
            return;
        }

        Emit(siloAddress, partitionIndex, version, range, operationName, heldDuration, canceled);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(
            SiloAddress siloAddress,
            int partitionIndex,
            MembershipVersion version,
            RingRange range,
            string operationName,
            TimeSpan heldDuration,
            bool canceled)
        {
            Listener.Write(nameof(RangeOperationCompleted), new RangeOperationCompleted(
                siloAddress,
                partitionIndex,
                version,
                range,
                operationName,
                heldDuration,
                canceled));
        }
    }

    /// <summary>
    /// Event payload for when a safety lease hold is created for a dead silo.
    /// </summary>
    internal sealed class SiloLeaseHoldCreated(
        SiloAddress observerSiloAddress,
        SiloAddress deadSiloAddress,
        DateTimeOffset expiration) : GrainDirectoryEvent(observerSiloAddress, partitionIndex: -1, default, RingRange.Empty)
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
    internal sealed class RangeLeaseHoldCreated(
        SiloAddress observerSiloAddress,
        RingRange range,
        DateTimeOffset expiration) : GrainDirectoryEvent(observerSiloAddress, partitionIndex: -1, default, range)
    {
        /// <summary>
        /// The time when the lease hold expires.
        /// </summary>
        public readonly DateTimeOffset Expiration = expiration;
    }

    /// <summary>
    /// Event payload for when a registration is delayed by a dead-silo lease hold.
    /// </summary>
    internal sealed class RegistrationBlockedBySiloLease(
        SiloAddress observerSiloAddress,
        GrainId grainId,
        SiloAddress deadSiloAddress,
        DateTimeOffset expiration,
        TimeSpan retryAfter) : GrainDirectoryEvent(observerSiloAddress, partitionIndex: -1, default, RingRange.Empty)
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
    internal sealed class RegistrationBlockedByRangeLease(
        SiloAddress observerSiloAddress,
        GrainId grainId,
        RingRange range,
        DateTimeOffset expiration,
        TimeSpan retryAfter) : GrainDirectoryEvent(observerSiloAddress, partitionIndex: -1, default, range)
    {
        /// <summary>
        /// The grain whose registration was blocked.
        /// </summary>
        public readonly GrainId GrainId = grainId;

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
    internal sealed class OperationDelayedByLeaseHold(
        SiloAddress observerSiloAddress,
        GrainId grainId,
        string operation,
        TimeSpan retryAfter) : GrainDirectoryEvent(observerSiloAddress, partitionIndex: -1, default, RingRange.Empty)
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
