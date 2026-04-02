using System;
using System.Diagnostics;
using Orleans;
using Orleans.Runtime;

namespace Orleans.Diagnostics;

/// <summary>
/// Provides diagnostic listener names and event names for Orleans lifecycle events.
/// </summary>
/// <remarks>
/// These types are public but may change between minor versions. They are intended for
/// advanced scenarios such as simulation testing and diagnostics.
/// </remarks>
public static class OrleansLifecycleDiagnostics
{
    /// <summary>
    /// The name of the diagnostic listener for silo lifecycle events.
    /// </summary>
    public const string SiloLifecycleListenerName = "Orleans.SiloLifecycle";

    /// <summary>
    /// The name of the diagnostic listener for client lifecycle events.
    /// </summary>
    public const string ClientLifecycleListenerName = "Orleans.ClientLifecycle";

    /// <summary>
    /// Event names for silo lifecycle diagnostics.
    /// </summary>
    public static class EventNames
    {
        /// <summary>
        /// Event fired when a lifecycle stage is about to start.
        /// Payload: <see cref="LifecycleStageStartingEvent"/>
        /// </summary>
        public const string StageStarting = "StageStarting";

        /// <summary>
        /// Event fired when a lifecycle stage has completed successfully.
        /// Payload: <see cref="LifecycleStageCompletedEvent"/>
        /// </summary>
        public const string StageCompleted = "StageCompleted";

        /// <summary>
        /// Event fired when a lifecycle stage has failed.
        /// Payload: <see cref="LifecycleStageFailedEvent"/>
        /// </summary>
        public const string StageFailed = "StageFailed";

        /// <summary>
        /// Event fired when a lifecycle observer is about to start.
        /// Payload: <see cref="LifecycleObserverStartingEvent"/>
        /// </summary>
        public const string ObserverStarting = "ObserverStarting";

        /// <summary>
        /// Event fired when a lifecycle observer has completed.
        /// Payload: <see cref="LifecycleObserverCompletedEvent"/>
        /// </summary>
        public const string ObserverCompleted = "ObserverCompleted";

        /// <summary>
        /// Event fired when a lifecycle observer has failed.
        /// Payload: <see cref="LifecycleObserverFailedEvent"/>
        /// </summary>
        public const string ObserverFailed = "ObserverFailed";

        /// <summary>
        /// Event fired when a lifecycle stage is about to stop.
        /// Payload: <see cref="LifecycleStageStoppingEvent"/>
        /// </summary>
        public const string StageStopping = "StageStopping";

        /// <summary>
        /// Event fired when a lifecycle stage has stopped.
        /// Payload: <see cref="LifecycleStageStoppedEvent"/>
        /// </summary>
        public const string StageStopped = "StageStopped";

        /// <summary>
        /// Event fired when an observer is about to stop.
        /// Payload: <see cref="LifecycleObserverStoppingEvent"/>
        /// </summary>
        public const string ObserverStopping = "ObserverStopping";

        /// <summary>
        /// Event fired when an observer has stopped.
        /// Payload: <see cref="LifecycleObserverStoppedEvent"/>
        /// </summary>
        public const string ObserverStopped = "ObserverStopped";
    }
}

/// <summary>
/// Event payload for when a lifecycle stage is about to start.
/// </summary>
/// <param name="Stage">The numeric stage identifier.</param>
/// <param name="StageName">The human-readable name of the stage.</param>
/// <param name="SiloAddress">The address of the silo (null for client lifecycle).</param>
/// <param name="Lifecycle">The lifecycle instance.</param>
public class LifecycleStageStartingEvent(
    int Stage,
    string StageName,
    SiloAddress? SiloAddress,
    ILifecycleObservable Lifecycle)
{
    public int Stage { get; } = Stage;
    public string StageName { get; } = StageName;
    public SiloAddress? SiloAddress { get; } = SiloAddress;
    public ILifecycleObservable Lifecycle { get; } = Lifecycle;
}

/// <summary>
/// Event payload for when a lifecycle stage has completed successfully.
/// </summary>
/// <param name="Stage">The numeric stage identifier.</param>
/// <param name="StageName">The human-readable name of the stage.</param>
/// <param name="SiloAddress">The address of the silo (null for client lifecycle).</param>
/// <param name="Elapsed">The time taken to complete the stage.</param>
/// <param name="Lifecycle">The lifecycle instance.</param>
public class LifecycleStageCompletedEvent(
    int Stage,
    string StageName,
    SiloAddress? SiloAddress,
    TimeSpan Elapsed,
    ILifecycleObservable Lifecycle)
{
    public int Stage { get; } = Stage;
    public string StageName { get; } = StageName;
    public SiloAddress? SiloAddress { get; } = SiloAddress;
    public TimeSpan Elapsed { get; } = Elapsed;
    public ILifecycleObservable Lifecycle { get; } = Lifecycle;
}

/// <summary>
/// Event payload for when a lifecycle stage has failed.
/// </summary>
/// <param name="Stage">The numeric stage identifier.</param>
/// <param name="StageName">The human-readable name of the stage.</param>
/// <param name="SiloAddress">The address of the silo (null for client lifecycle).</param>
/// <param name="Exception">The exception that caused the failure.</param>
/// <param name="Elapsed">The time elapsed before the failure.</param>
/// <param name="Lifecycle">The lifecycle instance.</param>
public class LifecycleStageFailedEvent(
    int Stage,
    string StageName,
    SiloAddress? SiloAddress,
    Exception Exception,
    TimeSpan Elapsed,
    ILifecycleObservable Lifecycle)
{
    public int Stage { get; } = Stage;
    public string StageName { get; } = StageName;
    public SiloAddress? SiloAddress { get; } = SiloAddress;
    public Exception Exception { get; } = Exception;
    public TimeSpan Elapsed { get; } = Elapsed;
    public ILifecycleObservable Lifecycle { get; } = Lifecycle;
}

/// <summary>
/// Event payload for when a lifecycle observer is about to start.
/// </summary>
/// <param name="ObserverName">The name of the observer.</param>
/// <param name="Stage">The numeric stage identifier.</param>
/// <param name="StageName">The human-readable name of the stage.</param>
/// <param name="SiloAddress">The address of the silo (null for client lifecycle).</param>
/// <param name="Observer">The lifecycle observer.</param>
public class LifecycleObserverStartingEvent(
    string ObserverName,
    int Stage,
    string StageName,
    SiloAddress? SiloAddress,
    ILifecycleObserver Observer)
{
    public string ObserverName { get; } = ObserverName;
    public int Stage { get; } = Stage;
    public string StageName { get; } = StageName;
    public SiloAddress? SiloAddress { get; } = SiloAddress;
    public ILifecycleObserver Observer { get; } = Observer;
}

/// <summary>
/// Event payload for when a lifecycle observer has completed.
/// </summary>
/// <param name="ObserverName">The name of the observer.</param>
/// <param name="Stage">The numeric stage identifier.</param>
/// <param name="StageName">The human-readable name of the stage.</param>
/// <param name="SiloAddress">The address of the silo (null for client lifecycle).</param>
/// <param name="Elapsed">The time taken to complete.</param>
/// <param name="Observer">The lifecycle observer.</param>
public class LifecycleObserverCompletedEvent(
    string ObserverName,
    int Stage,
    string StageName,
    SiloAddress? SiloAddress,
    TimeSpan Elapsed,
    ILifecycleObserver Observer)
{
    public string ObserverName { get; } = ObserverName;
    public int Stage { get; } = Stage;
    public string StageName { get; } = StageName;
    public SiloAddress? SiloAddress { get; } = SiloAddress;
    public TimeSpan Elapsed { get; } = Elapsed;
    public ILifecycleObserver Observer { get; } = Observer;
}

/// <summary>
/// Event payload for when a lifecycle observer has failed.
/// </summary>
/// <param name="ObserverName">The name of the observer.</param>
/// <param name="Stage">The numeric stage identifier.</param>
/// <param name="StageName">The human-readable name of the stage.</param>
/// <param name="SiloAddress">The address of the silo (null for client lifecycle).</param>
/// <param name="Exception">The exception that caused the failure.</param>
/// <param name="Elapsed">The time elapsed before the failure.</param>
/// <param name="Observer">The lifecycle observer.</param>
public class LifecycleObserverFailedEvent(
    string ObserverName,
    int Stage,
    string StageName,
    SiloAddress? SiloAddress,
    Exception Exception,
    TimeSpan Elapsed,
    ILifecycleObserver Observer)
{
    public string ObserverName { get; } = ObserverName;
    public int Stage { get; } = Stage;
    public string StageName { get; } = StageName;
    public SiloAddress? SiloAddress { get; } = SiloAddress;
    public Exception Exception { get; } = Exception;
    public TimeSpan Elapsed { get; } = Elapsed;
    public ILifecycleObserver Observer { get; } = Observer;
}

/// <summary>
/// Event payload for when a lifecycle stage is about to stop.
/// </summary>
/// <param name="Stage">The numeric stage identifier.</param>
/// <param name="StageName">The human-readable name of the stage.</param>
/// <param name="SiloAddress">The address of the silo (null for client lifecycle).</param>
/// <param name="Lifecycle">The lifecycle instance.</param>
public class LifecycleStageStoppingEvent(
    int Stage,
    string StageName,
    SiloAddress? SiloAddress,
    ILifecycleObservable Lifecycle)
{
    public int Stage { get; } = Stage;
    public string StageName { get; } = StageName;
    public SiloAddress? SiloAddress { get; } = SiloAddress;
    public ILifecycleObservable Lifecycle { get; } = Lifecycle;
}

/// <summary>
/// Event payload for when a lifecycle stage has stopped.
/// </summary>
/// <param name="Stage">The numeric stage identifier.</param>
/// <param name="StageName">The human-readable name of the stage.</param>
/// <param name="SiloAddress">The address of the silo (null for client lifecycle).</param>
/// <param name="Elapsed">The time taken to stop the stage.</param>
/// <param name="Lifecycle">The lifecycle instance.</param>
public class LifecycleStageStoppedEvent(
    int Stage,
    string StageName,
    SiloAddress? SiloAddress,
    TimeSpan Elapsed,
    ILifecycleObservable Lifecycle)
{
    public int Stage { get; } = Stage;
    public string StageName { get; } = StageName;
    public SiloAddress? SiloAddress { get; } = SiloAddress;
    public TimeSpan Elapsed { get; } = Elapsed;
    public ILifecycleObservable Lifecycle { get; } = Lifecycle;
}

/// <summary>
/// Event payload for when a lifecycle observer is about to stop.
/// </summary>
/// <param name="ObserverName">The name of the observer.</param>
/// <param name="Stage">The numeric stage identifier.</param>
/// <param name="StageName">The human-readable name of the stage.</param>
/// <param name="SiloAddress">The address of the silo (null for client lifecycle).</param>
/// <param name="Observer">The lifecycle observer.</param>
public class LifecycleObserverStoppingEvent(
    string ObserverName,
    int Stage,
    string StageName,
    SiloAddress? SiloAddress,
    ILifecycleObserver Observer)
{
    public string ObserverName { get; } = ObserverName;
    public int Stage { get; } = Stage;
    public string StageName { get; } = StageName;
    public SiloAddress? SiloAddress { get; } = SiloAddress;
    public ILifecycleObserver Observer { get; } = Observer;
}

/// <summary>
/// Event payload for when a lifecycle observer has stopped.
/// </summary>
/// <param name="ObserverName">The name of the observer.</param>
/// <param name="Stage">The numeric stage identifier.</param>
/// <param name="StageName">The human-readable name of the stage.</param>
/// <param name="SiloAddress">The address of the silo (null for client lifecycle).</param>
/// <param name="Elapsed">The time taken to stop.</param>
/// <param name="Observer">The lifecycle observer.</param>
public class LifecycleObserverStoppedEvent(
    string ObserverName,
    int Stage,
    string StageName,
    SiloAddress? SiloAddress,
    TimeSpan Elapsed,
    ILifecycleObserver Observer)
{
    public string ObserverName { get; } = ObserverName;
    public int Stage { get; } = Stage;
    public string StageName { get; } = StageName;
    public SiloAddress? SiloAddress { get; } = SiloAddress;
    public TimeSpan Elapsed { get; } = Elapsed;
    public ILifecycleObserver Observer { get; } = Observer;
}

internal static class OrleansLifecycleDiagnosticListener
{
    private static readonly DiagnosticListener Listener = new(OrleansLifecycleDiagnostics.SiloLifecycleListenerName);

    internal static void EmitStageCompleted(int stage, string stageName, SiloAddress? siloAddress, TimeSpan elapsed, ILifecycleObservable lifecycle)
    {
        if (!Listener.IsEnabled(OrleansLifecycleDiagnostics.EventNames.StageCompleted))
        {
            return;
        }

        Emit(Listener, stage, stageName, siloAddress, elapsed, lifecycle);

        static void Emit(DiagnosticListener listener, int stage, string stageName, SiloAddress? siloAddress, TimeSpan elapsed, ILifecycleObservable lifecycle)
        {
            listener.Write(OrleansLifecycleDiagnostics.EventNames.StageCompleted, new LifecycleStageCompletedEvent(
                stage,
                stageName,
                siloAddress,
                elapsed,
                lifecycle));
        }
    }

    internal static void EmitStageStopped(int stage, string stageName, SiloAddress? siloAddress, TimeSpan elapsed, ILifecycleObservable lifecycle)
    {
        if (!Listener.IsEnabled(OrleansLifecycleDiagnostics.EventNames.StageStopped))
        {
            return;
        }

        Emit(Listener, stage, stageName, siloAddress, elapsed, lifecycle);

        static void Emit(DiagnosticListener listener, int stage, string stageName, SiloAddress? siloAddress, TimeSpan elapsed, ILifecycleObservable lifecycle)
        {
            listener.Write(OrleansLifecycleDiagnostics.EventNames.StageStopped, new LifecycleStageStoppedEvent(
                stage,
                stageName,
                siloAddress,
                elapsed,
                lifecycle));
        }
    }

    internal static void EmitObserverCompleted(string observerName, int stage, string stageName, SiloAddress? siloAddress, TimeSpan elapsed, ILifecycleObserver observer)
    {
        if (!Listener.IsEnabled(OrleansLifecycleDiagnostics.EventNames.ObserverCompleted))
        {
            return;
        }

        Emit(Listener, observerName, stage, stageName, siloAddress, elapsed, observer);

        static void Emit(DiagnosticListener listener, string observerName, int stage, string stageName, SiloAddress? siloAddress, TimeSpan elapsed, ILifecycleObserver observer)
        {
            listener.Write(OrleansLifecycleDiagnostics.EventNames.ObserverCompleted, new LifecycleObserverCompletedEvent(
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
        if (!Listener.IsEnabled(OrleansLifecycleDiagnostics.EventNames.ObserverFailed))
        {
            return;
        }

        Emit(Listener, observerName, stage, stageName, siloAddress, exception, elapsed, observer);

        static void Emit(DiagnosticListener listener, string observerName, int stage, string stageName, SiloAddress? siloAddress, Exception exception, TimeSpan elapsed, ILifecycleObserver observer)
        {
            listener.Write(OrleansLifecycleDiagnostics.EventNames.ObserverFailed, new LifecycleObserverFailedEvent(
                observerName,
                stage,
                stageName,
                siloAddress,
                exception,
                elapsed,
                observer));
        }
    }

    internal static void EmitObserverStarting(string observerName, int stage, string stageName, SiloAddress? siloAddress, ILifecycleObserver observer)
    {
        if (!Listener.IsEnabled(OrleansLifecycleDiagnostics.EventNames.ObserverStarting))
        {
            return;
        }

        Emit(Listener, observerName, stage, stageName, siloAddress, observer);

        static void Emit(DiagnosticListener listener, string observerName, int stage, string stageName, SiloAddress? siloAddress, ILifecycleObserver observer)
        {
            listener.Write(OrleansLifecycleDiagnostics.EventNames.ObserverStarting, new LifecycleObserverStartingEvent(
                observerName,
                stage,
                stageName,
                siloAddress,
                observer));
        }
    }

    internal static void EmitObserverStopped(string observerName, int stage, string stageName, SiloAddress? siloAddress, TimeSpan elapsed, ILifecycleObserver observer)
    {
        if (!Listener.IsEnabled(OrleansLifecycleDiagnostics.EventNames.ObserverStopped))
        {
            return;
        }

        Emit(Listener, observerName, stage, stageName, siloAddress, elapsed, observer);

        static void Emit(DiagnosticListener listener, string observerName, int stage, string stageName, SiloAddress? siloAddress, TimeSpan elapsed, ILifecycleObserver observer)
        {
            listener.Write(OrleansLifecycleDiagnostics.EventNames.ObserverStopped, new LifecycleObserverStoppedEvent(
                observerName,
                stage,
                stageName,
                siloAddress,
                elapsed,
                observer));
        }
    }

    internal static void EmitObserverStopping(string observerName, int stage, string stageName, SiloAddress? siloAddress, ILifecycleObserver observer)
    {
        if (!Listener.IsEnabled(OrleansLifecycleDiagnostics.EventNames.ObserverStopping))
        {
            return;
        }

        Emit(Listener, observerName, stage, stageName, siloAddress, observer);

        static void Emit(DiagnosticListener listener, string observerName, int stage, string stageName, SiloAddress? siloAddress, ILifecycleObserver observer)
        {
            listener.Write(OrleansLifecycleDiagnostics.EventNames.ObserverStopping, new LifecycleObserverStoppingEvent(
                observerName,
                stage,
                stageName,
                siloAddress,
                observer));
        }
    }
}
