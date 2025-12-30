using System;

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
public record LifecycleStageStartingEvent(
    int Stage,
    string StageName,
    SiloAddress? SiloAddress);

/// <summary>
/// Event payload for when a lifecycle stage has completed successfully.
/// </summary>
/// <param name="Stage">The numeric stage identifier.</param>
/// <param name="StageName">The human-readable name of the stage.</param>
/// <param name="SiloAddress">The address of the silo (null for client lifecycle).</param>
/// <param name="Elapsed">The time taken to complete the stage.</param>
public record LifecycleStageCompletedEvent(
    int Stage,
    string StageName,
    SiloAddress? SiloAddress,
    TimeSpan Elapsed);

/// <summary>
/// Event payload for when a lifecycle stage has failed.
/// </summary>
/// <param name="Stage">The numeric stage identifier.</param>
/// <param name="StageName">The human-readable name of the stage.</param>
/// <param name="SiloAddress">The address of the silo (null for client lifecycle).</param>
/// <param name="Exception">The exception that caused the failure.</param>
/// <param name="Elapsed">The time elapsed before the failure.</param>
public record LifecycleStageFailedEvent(
    int Stage,
    string StageName,
    SiloAddress? SiloAddress,
    Exception Exception,
    TimeSpan Elapsed);

/// <summary>
/// Event payload for when a lifecycle observer is about to start.
/// </summary>
/// <param name="ObserverName">The name of the observer.</param>
/// <param name="Stage">The numeric stage identifier.</param>
/// <param name="StageName">The human-readable name of the stage.</param>
/// <param name="SiloAddress">The address of the silo (null for client lifecycle).</param>
public record LifecycleObserverStartingEvent(
    string ObserverName,
    int Stage,
    string StageName,
    SiloAddress? SiloAddress);

/// <summary>
/// Event payload for when a lifecycle observer has completed.
/// </summary>
/// <param name="ObserverName">The name of the observer.</param>
/// <param name="Stage">The numeric stage identifier.</param>
/// <param name="StageName">The human-readable name of the stage.</param>
/// <param name="SiloAddress">The address of the silo (null for client lifecycle).</param>
/// <param name="Elapsed">The time taken to complete.</param>
public record LifecycleObserverCompletedEvent(
    string ObserverName,
    int Stage,
    string StageName,
    SiloAddress? SiloAddress,
    TimeSpan Elapsed);

/// <summary>
/// Event payload for when a lifecycle observer has failed.
/// </summary>
/// <param name="ObserverName">The name of the observer.</param>
/// <param name="Stage">The numeric stage identifier.</param>
/// <param name="StageName">The human-readable name of the stage.</param>
/// <param name="SiloAddress">The address of the silo (null for client lifecycle).</param>
/// <param name="Exception">The exception that caused the failure.</param>
/// <param name="Elapsed">The time elapsed before the failure.</param>
public record LifecycleObserverFailedEvent(
    string ObserverName,
    int Stage,
    string StageName,
    SiloAddress? SiloAddress,
    Exception Exception,
    TimeSpan Elapsed);

/// <summary>
/// Event payload for when a lifecycle stage is about to stop.
/// </summary>
/// <param name="Stage">The numeric stage identifier.</param>
/// <param name="StageName">The human-readable name of the stage.</param>
/// <param name="SiloAddress">The address of the silo (null for client lifecycle).</param>
public record LifecycleStageStoppingEvent(
    int Stage,
    string StageName,
    SiloAddress? SiloAddress);

/// <summary>
/// Event payload for when a lifecycle stage has stopped.
/// </summary>
/// <param name="Stage">The numeric stage identifier.</param>
/// <param name="StageName">The human-readable name of the stage.</param>
/// <param name="SiloAddress">The address of the silo (null for client lifecycle).</param>
/// <param name="Elapsed">The time taken to stop the stage.</param>
public record LifecycleStageStoppedEvent(
    int Stage,
    string StageName,
    SiloAddress? SiloAddress,
    TimeSpan Elapsed);

/// <summary>
/// Event payload for when a lifecycle observer is about to stop.
/// </summary>
/// <param name="ObserverName">The name of the observer.</param>
/// <param name="Stage">The numeric stage identifier.</param>
/// <param name="StageName">The human-readable name of the stage.</param>
/// <param name="SiloAddress">The address of the silo (null for client lifecycle).</param>
public record LifecycleObserverStoppingEvent(
    string ObserverName,
    int Stage,
    string StageName,
    SiloAddress? SiloAddress);

/// <summary>
/// Event payload for when a lifecycle observer has stopped.
/// </summary>
/// <param name="ObserverName">The name of the observer.</param>
/// <param name="Stage">The numeric stage identifier.</param>
/// <param name="StageName">The human-readable name of the stage.</param>
/// <param name="SiloAddress">The address of the silo (null for client lifecycle).</param>
/// <param name="Elapsed">The time taken to stop.</param>
public record LifecycleObserverStoppedEvent(
    string ObserverName,
    int Stage,
    string StageName,
    SiloAddress? SiloAddress,
    TimeSpan Elapsed);
