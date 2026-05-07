using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Orleans.Runtime.Diagnostics;

/// <summary>
/// Provides the diagnostic listener and event payload types for Orleans silo lifecycle events.
/// </summary>
/// <remarks>
/// These types are public but may change between minor versions. They are intended for
/// advanced scenarios such as simulation testing and diagnostics.
/// </remarks>
public static class SiloLifecycleEvents
{
    /// <summary>
    /// The name of the diagnostic listener for silo lifecycle events.
    /// </summary>
    public const string ListenerName = "Orleans.SiloLifecycle";

    private static readonly DiagnosticListener Listener = new(ListenerName);

    /// <summary>
    /// Gets an observable sequence of all lifecycle events.
    /// </summary>
    public static IObservable<LifecycleEvent> AllEvents { get; } = new Observable();

    /// <summary>
    /// The base class used for lifecycle diagnostic events.
    /// </summary>
    public abstract class LifecycleEvent(
        int stage,
        string stageName,
        SiloAddress? siloAddress)
    {
        /// <summary>
        /// The numeric stage identifier.
        /// </summary>
        public readonly int Stage = stage;

        /// <summary>
        /// The human-readable stage name.
        /// </summary>
        public readonly string StageName = stageName;

        /// <summary>
        /// The silo address associated with the event, if any.
        /// </summary>
        public readonly SiloAddress? SiloAddress = siloAddress;
    }

    /// <summary>
    /// Event payload for when a lifecycle stage is about to start.
    /// </summary>
    /// <param name="stage">The numeric stage identifier.</param>
    /// <param name="stageName">The human-readable stage name.</param>
    /// <param name="siloAddress">The silo address associated with the event, if any.</param>
    /// <param name="lifecycle">The lifecycle instance.</param>
    public sealed class StageStarting(
        int stage,
        string stageName,
        SiloAddress? siloAddress,
        ILifecycleObservable lifecycle) : LifecycleEvent(stage, stageName, siloAddress)
    {
        /// <summary>
        /// The lifecycle instance.
        /// </summary>
        public readonly ILifecycleObservable Lifecycle = lifecycle;
    }

    /// <summary>
    /// Event payload for when a lifecycle stage has completed successfully.
    /// </summary>
    /// <param name="stage">The numeric stage identifier.</param>
    /// <param name="stageName">The human-readable stage name.</param>
    /// <param name="siloAddress">The silo address associated with the event, if any.</param>
    /// <param name="elapsed">The time taken to complete the stage.</param>
    /// <param name="lifecycle">The lifecycle instance.</param>
    public sealed class StageCompleted(
        int stage,
        string stageName,
        SiloAddress? siloAddress,
        TimeSpan elapsed,
        ILifecycleObservable lifecycle) : LifecycleEvent(stage, stageName, siloAddress)
    {
        /// <summary>
        /// The time taken to complete the stage.
        /// </summary>
        public readonly TimeSpan Elapsed = elapsed;

        /// <summary>
        /// The lifecycle instance.
        /// </summary>
        public readonly ILifecycleObservable Lifecycle = lifecycle;
    }

    /// <summary>
    /// Event payload for when a lifecycle stage has failed.
    /// </summary>
    /// <param name="stage">The numeric stage identifier.</param>
    /// <param name="stageName">The human-readable stage name.</param>
    /// <param name="siloAddress">The silo address associated with the event, if any.</param>
    /// <param name="exception">The exception which caused the failure.</param>
    /// <param name="elapsed">The time elapsed before the failure.</param>
    /// <param name="lifecycle">The lifecycle instance.</param>
    public sealed class StageFailed(
        int stage,
        string stageName,
        SiloAddress? siloAddress,
        Exception exception,
        TimeSpan elapsed,
        ILifecycleObservable lifecycle) : LifecycleEvent(stage, stageName, siloAddress)
    {
        /// <summary>
        /// The exception which caused the failure.
        /// </summary>
        public readonly Exception Exception = exception;

        /// <summary>
        /// The time elapsed before the failure.
        /// </summary>
        public readonly TimeSpan Elapsed = elapsed;

        /// <summary>
        /// The lifecycle instance.
        /// </summary>
        public readonly ILifecycleObservable Lifecycle = lifecycle;
    }

    /// <summary>
    /// Event payload for when a lifecycle observer is about to start.
    /// </summary>
    /// <param name="observerName">The observer name.</param>
    /// <param name="stage">The numeric stage identifier.</param>
    /// <param name="stageName">The human-readable stage name.</param>
    /// <param name="siloAddress">The silo address associated with the event, if any.</param>
    /// <param name="observer">The lifecycle observer.</param>
    public sealed class ObserverStarting(
        string observerName,
        int stage,
        string stageName,
        SiloAddress? siloAddress,
        ILifecycleObserver observer) : LifecycleEvent(stage, stageName, siloAddress)
    {
        /// <summary>
        /// The observer name.
        /// </summary>
        public readonly string ObserverName = observerName;

        /// <summary>
        /// The lifecycle observer.
        /// </summary>
        public readonly ILifecycleObserver Observer = observer;
    }

    /// <summary>
    /// Event payload for when a lifecycle observer has completed successfully.
    /// </summary>
    /// <param name="observerName">The observer name.</param>
    /// <param name="stage">The numeric stage identifier.</param>
    /// <param name="stageName">The human-readable stage name.</param>
    /// <param name="siloAddress">The silo address associated with the event, if any.</param>
    /// <param name="elapsed">The time taken to complete.</param>
    /// <param name="observer">The lifecycle observer.</param>
    public sealed class ObserverCompleted(
        string observerName,
        int stage,
        string stageName,
        SiloAddress? siloAddress,
        TimeSpan elapsed,
        ILifecycleObserver observer) : LifecycleEvent(stage, stageName, siloAddress)
    {
        /// <summary>
        /// The observer name.
        /// </summary>
        public readonly string ObserverName = observerName;

        /// <summary>
        /// The time taken to complete.
        /// </summary>
        public readonly TimeSpan Elapsed = elapsed;

        /// <summary>
        /// The lifecycle observer.
        /// </summary>
        public readonly ILifecycleObserver Observer = observer;
    }

    /// <summary>
    /// Event payload for when a lifecycle observer has failed.
    /// </summary>
    /// <param name="observerName">The observer name.</param>
    /// <param name="stage">The numeric stage identifier.</param>
    /// <param name="stageName">The human-readable stage name.</param>
    /// <param name="siloAddress">The silo address associated with the event, if any.</param>
    /// <param name="exception">The exception which caused the failure.</param>
    /// <param name="elapsed">The time elapsed before the failure.</param>
    /// <param name="observer">The lifecycle observer.</param>
    public sealed class ObserverFailed(
        string observerName,
        int stage,
        string stageName,
        SiloAddress? siloAddress,
        Exception exception,
        TimeSpan elapsed,
        ILifecycleObserver observer) : LifecycleEvent(stage, stageName, siloAddress)
    {
        /// <summary>
        /// The observer name.
        /// </summary>
        public readonly string ObserverName = observerName;

        /// <summary>
        /// The exception which caused the failure.
        /// </summary>
        public readonly Exception Exception = exception;

        /// <summary>
        /// The time elapsed before the failure.
        /// </summary>
        public readonly TimeSpan Elapsed = elapsed;

        /// <summary>
        /// The lifecycle observer.
        /// </summary>
        public readonly ILifecycleObserver Observer = observer;
    }

    /// <summary>
    /// Event payload for when a lifecycle stage is about to stop.
    /// </summary>
    /// <param name="stage">The numeric stage identifier.</param>
    /// <param name="stageName">The human-readable stage name.</param>
    /// <param name="siloAddress">The silo address associated with the event, if any.</param>
    /// <param name="lifecycle">The lifecycle instance.</param>
    public sealed class StageStopping(
        int stage,
        string stageName,
        SiloAddress? siloAddress,
        ILifecycleObservable lifecycle) : LifecycleEvent(stage, stageName, siloAddress)
    {
        /// <summary>
        /// The lifecycle instance.
        /// </summary>
        public readonly ILifecycleObservable Lifecycle = lifecycle;
    }

    /// <summary>
    /// Event payload for when a lifecycle stage has stopped.
    /// </summary>
    /// <param name="stage">The numeric stage identifier.</param>
    /// <param name="stageName">The human-readable stage name.</param>
    /// <param name="siloAddress">The silo address associated with the event, if any.</param>
    /// <param name="elapsed">The time taken to stop the stage.</param>
    /// <param name="lifecycle">The lifecycle instance.</param>
    public sealed class StageStopped(
        int stage,
        string stageName,
        SiloAddress? siloAddress,
        TimeSpan elapsed,
        ILifecycleObservable lifecycle) : LifecycleEvent(stage, stageName, siloAddress)
    {
        /// <summary>
        /// The time taken to stop the stage.
        /// </summary>
        public readonly TimeSpan Elapsed = elapsed;

        /// <summary>
        /// The lifecycle instance.
        /// </summary>
        public readonly ILifecycleObservable Lifecycle = lifecycle;
    }

    /// <summary>
    /// Event payload for when a lifecycle observer is about to stop.
    /// </summary>
    /// <param name="observerName">The observer name.</param>
    /// <param name="stage">The numeric stage identifier.</param>
    /// <param name="stageName">The human-readable stage name.</param>
    /// <param name="siloAddress">The silo address associated with the event, if any.</param>
    /// <param name="observer">The lifecycle observer.</param>
    public sealed class ObserverStopping(
        string observerName,
        int stage,
        string stageName,
        SiloAddress? siloAddress,
        ILifecycleObserver observer) : LifecycleEvent(stage, stageName, siloAddress)
    {
        /// <summary>
        /// The observer name.
        /// </summary>
        public readonly string ObserverName = observerName;

        /// <summary>
        /// The lifecycle observer.
        /// </summary>
        public readonly ILifecycleObserver Observer = observer;
    }

    /// <summary>
    /// Event payload for when a lifecycle observer has stopped.
    /// </summary>
    /// <param name="observerName">The observer name.</param>
    /// <param name="stage">The numeric stage identifier.</param>
    /// <param name="stageName">The human-readable stage name.</param>
    /// <param name="siloAddress">The silo address associated with the event, if any.</param>
    /// <param name="elapsed">The time taken to stop.</param>
    /// <param name="observer">The lifecycle observer.</param>
    public sealed class ObserverStopped(
        string observerName,
        int stage,
        string stageName,
        SiloAddress? siloAddress,
        TimeSpan elapsed,
        ILifecycleObserver observer) : LifecycleEvent(stage, stageName, siloAddress)
    {
        /// <summary>
        /// The observer name.
        /// </summary>
        public readonly string ObserverName = observerName;

        /// <summary>
        /// The time taken to stop.
        /// </summary>
        public readonly TimeSpan Elapsed = elapsed;

        /// <summary>
        /// The lifecycle observer.
        /// </summary>
        public readonly ILifecycleObserver Observer = observer;
    }

    internal static void EmitObserverStarting(string observerName, int stage, string stageName, SiloAddress? siloAddress, ILifecycleObserver observer)
    {
        if (!Listener.IsEnabled(nameof(ObserverStarting)))
        {
            return;
        }

        Emit(observerName, stage, stageName, siloAddress, observer);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(string observerName, int stage, string stageName, SiloAddress? siloAddress, ILifecycleObserver observer)
        {
            Listener.Write(nameof(ObserverStarting), new ObserverStarting(
                observerName,
                stage,
                stageName,
                siloAddress,
                observer));
        }
    }

    internal static void EmitObserverCompleted(string observerName, int stage, string stageName, SiloAddress? siloAddress, TimeSpan elapsed, ILifecycleObserver observer)
    {
        if (!Listener.IsEnabled(nameof(ObserverCompleted)))
        {
            return;
        }

        Emit(observerName, stage, stageName, siloAddress, elapsed, observer);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(string observerName, int stage, string stageName, SiloAddress? siloAddress, TimeSpan elapsed, ILifecycleObserver observer)
        {
            Listener.Write(nameof(ObserverCompleted), new ObserverCompleted(
                observerName,
                stage,
                stageName,
                siloAddress,
                elapsed,
                observer));
        }
    }

    internal static void EmitObserverFailed(string observerName, int stage, string stageName, SiloAddress? siloAddress, Exception exception, TimeSpan elapsed, ILifecycleObserver observer)
    {
        if (!Listener.IsEnabled(nameof(ObserverFailed)))
        {
            return;
        }

        Emit(observerName, stage, stageName, siloAddress, exception, elapsed, observer);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(string observerName, int stage, string stageName, SiloAddress? siloAddress, Exception exception, TimeSpan elapsed, ILifecycleObserver observer)
        {
            Listener.Write(nameof(ObserverFailed), new ObserverFailed(
                observerName,
                stage,
                stageName,
                siloAddress,
                exception,
                elapsed,
                observer));
        }
    }

    internal static void EmitObserverStopping(string observerName, int stage, string stageName, SiloAddress? siloAddress, ILifecycleObserver observer)
    {
        if (!Listener.IsEnabled(nameof(ObserverStopping)))
        {
            return;
        }

        Emit(observerName, stage, stageName, siloAddress, observer);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(string observerName, int stage, string stageName, SiloAddress? siloAddress, ILifecycleObserver observer)
        {
            Listener.Write(nameof(ObserverStopping), new ObserverStopping(
                observerName,
                stage,
                stageName,
                siloAddress,
                observer));
        }
    }

    internal static void EmitObserverStopped(string observerName, int stage, string stageName, SiloAddress? siloAddress, TimeSpan elapsed, ILifecycleObserver observer)
    {
        if (!Listener.IsEnabled(nameof(ObserverStopped)))
        {
            return;
        }

        Emit(observerName, stage, stageName, siloAddress, elapsed, observer);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(string observerName, int stage, string stageName, SiloAddress? siloAddress, TimeSpan elapsed, ILifecycleObserver observer)
        {
            Listener.Write(nameof(ObserverStopped), new ObserverStopped(
                observerName,
                stage,
                stageName,
                siloAddress,
                elapsed,
                observer));
        }
    }

    internal static void EmitStageCompleted(int stage, string stageName, SiloAddress? siloAddress, TimeSpan elapsed, ILifecycleObservable lifecycle)
    {
        if (!Listener.IsEnabled(nameof(StageCompleted)))
        {
            return;
        }

        Emit(stage, stageName, siloAddress, elapsed, lifecycle);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(int stage, string stageName, SiloAddress? siloAddress, TimeSpan elapsed, ILifecycleObservable lifecycle)
        {
            Listener.Write(nameof(StageCompleted), new StageCompleted(
                stage,
                stageName,
                siloAddress,
                elapsed,
                lifecycle));
        }
    }

    internal static void EmitStageStopped(int stage, string stageName, SiloAddress? siloAddress, TimeSpan elapsed, ILifecycleObservable lifecycle)
    {
        if (!Listener.IsEnabled(nameof(StageStopped)))
        {
            return;
        }

        Emit(stage, stageName, siloAddress, elapsed, lifecycle);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(int stage, string stageName, SiloAddress? siloAddress, TimeSpan elapsed, ILifecycleObservable lifecycle)
        {
            Listener.Write(nameof(StageStopped), new StageStopped(
                stage,
                stageName,
                siloAddress,
                elapsed,
                lifecycle));
        }
    }

    private sealed class Observable : IObservable<LifecycleEvent>
    {
        public IDisposable Subscribe(IObserver<LifecycleEvent> observer) => Listener.Subscribe(new Observer(observer));

        private sealed class Observer(IObserver<LifecycleEvent> observer) : IObserver<KeyValuePair<string, object?>>
        {
            public void OnCompleted() => observer.OnCompleted();
            public void OnError(Exception error) => observer.OnError(error);

            public void OnNext(KeyValuePair<string, object?> value)
            {
                if (value.Value is LifecycleEvent evt)
                {
                    observer.OnNext(evt);
                }
            }
        }
    }
}
