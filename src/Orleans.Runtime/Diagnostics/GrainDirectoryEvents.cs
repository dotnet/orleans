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
