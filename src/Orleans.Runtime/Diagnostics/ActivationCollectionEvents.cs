using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Orleans.Runtime.Diagnostics;

internal static class ActivationCollectionEvents
{
    public const string ListenerName = "Orleans.ActivationCollection";

    private static readonly DiagnosticListener Listener = new(ListenerName);

    public static IObservable<CollectionEvent> AllEvents { get; } = new Observable();

    internal static bool IsCollectionCompletedEnabled => Listener.IsEnabled(nameof(CollectionCompleted));

    public enum CollectionSource
    {
        Stale,
        AgeLimit,
        MemoryPressure
    }

    public abstract class CollectionEvent(CollectionSource source, TimeSpan ageLimit, DeactivationReason reason)
    {
        public readonly CollectionSource Source = source;
        public readonly TimeSpan AgeLimit = ageLimit;
        public readonly DeactivationReason Reason = reason;
    }

    public sealed class CollectionCompleted(
        CollectionSource source,
        TimeSpan ageLimit,
        DeactivationReason reason,
        IReadOnlyList<CollectedActivation> activations) : CollectionEvent(source, ageLimit, reason)
    {
        public readonly IReadOnlyList<CollectedActivation> Activations = activations;
    }

    public readonly struct CollectedActivation(GrainId grainId, ActivationId activationId)
    {
        public readonly GrainId GrainId = grainId;
        public readonly ActivationId ActivationId = activationId;
    }

    internal static void EmitCollectionCompleted(
        CollectionSource source,
        TimeSpan ageLimit,
        DeactivationReason reason,
        IReadOnlyList<CollectedActivation> activations)
    {
        if (!Listener.IsEnabled(nameof(CollectionCompleted)))
        {
            return;
        }

        Emit(source, ageLimit, reason, activations);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(
            CollectionSource source,
            TimeSpan ageLimit,
            DeactivationReason reason,
            IReadOnlyList<CollectedActivation> activations)
        {
            Listener.Write(nameof(CollectionCompleted), new CollectionCompleted(
                source,
                ageLimit,
                reason,
                activations));
        }
    }

    private sealed class Observable : IObservable<CollectionEvent>
    {
        public IDisposable Subscribe(IObserver<CollectionEvent> observer) => Listener.Subscribe(new Observer(observer));

        private sealed class Observer(IObserver<CollectionEvent> observer) : IObserver<KeyValuePair<string, object?>>
        {
            public void OnCompleted() => observer.OnCompleted();
            public void OnError(Exception error) => observer.OnError(error);

            public void OnNext(KeyValuePair<string, object?> value)
            {
                if (value.Value is CollectionEvent evt)
                {
                    observer.OnNext(evt);
                }
            }
        }
    }
}
