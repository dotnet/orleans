using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Orleans.Runtime.Diagnostics;

internal static class PlacementServiceEvents
{
    internal const string ListenerName = "Orleans.PlacementService";

    private static readonly DiagnosticListener Listener = new(ListenerName);

    internal static IObservable<PlacementServiceEvent> AllEvents { get; } = new Observable();

    internal abstract class PlacementServiceEvent(SiloAddress siloAddress, int workerIndex)
    {
        public readonly SiloAddress SiloAddress = siloAddress;
        public readonly int WorkerIndex = workerIndex;
    }

    internal sealed class WorkerStopped(SiloAddress siloAddress, int workerIndex)
        : PlacementServiceEvent(siloAddress, workerIndex);

    internal static void EmitWorkerStopped(SiloAddress siloAddress, int workerIndex)
    {
        if (!Listener.IsEnabled(nameof(WorkerStopped)))
        {
            return;
        }

        Emit(siloAddress, workerIndex);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(SiloAddress siloAddress, int workerIndex)
        {
            Listener.Write(nameof(WorkerStopped), new WorkerStopped(siloAddress, workerIndex));
        }
    }

    private sealed class Observable : IObservable<PlacementServiceEvent>
    {
        public IDisposable Subscribe(IObserver<PlacementServiceEvent> observer) => Listener.Subscribe(new Observer(observer));

        private sealed class Observer(IObserver<PlacementServiceEvent> observer) : IObserver<KeyValuePair<string, object?>>
        {
            public void OnCompleted() => observer.OnCompleted();
            public void OnError(Exception error) => observer.OnError(error);

            public void OnNext(KeyValuePair<string, object?> value)
            {
                if (value.Value is PlacementServiceEvent evt)
                {
                    observer.OnNext(evt);
                }
            }
        }
    }
}
