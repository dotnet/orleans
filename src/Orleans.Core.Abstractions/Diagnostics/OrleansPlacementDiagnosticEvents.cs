using System;
using Orleans.Runtime;

namespace Orleans.Diagnostics;

/// <summary>
/// Provides diagnostic listener names and event names for Orleans placement and load statistics events.
/// </summary>
/// <remarks>
/// These types are public but may change between minor versions. They are intended for
/// advanced scenarios such as simulation testing and diagnostics.
/// </remarks>
public static class OrleansPlacementDiagnostics
{
    /// <summary>
    /// The name of the diagnostic listener for placement and load statistics events.
    /// </summary>
    public const string ListenerName = "Orleans.Placement";

    /// <summary>
    /// Event names for placement diagnostics.
    /// </summary>
    public static class EventNames
    {
        /// <summary>
        /// Event fired when a silo publishes its runtime statistics to the cluster.
        /// Payload: <see cref="SiloStatisticsPublishedEvent"/>
        /// </summary>
        public const string StatisticsPublished = "StatisticsPublished";

        /// <summary>
        /// Event fired when a silo receives runtime statistics from another silo.
        /// Payload: <see cref="SiloStatisticsReceivedEvent"/>
        /// </summary>
        public const string StatisticsReceived = "StatisticsReceived";

        /// <summary>
        /// Event fired when a silo completes refreshing statistics from all cluster members.
        /// Payload: <see cref="ClusterStatisticsRefreshedEvent"/>
        /// </summary>
        public const string ClusterStatisticsRefreshed = "ClusterStatisticsRefreshed";

        /// <summary>
        /// Event fired when a silo's statistics are removed (e.g., when the silo leaves the cluster).
        /// Payload: <see cref="SiloStatisticsRemovedEvent"/>
        /// </summary>
        public const string StatisticsRemoved = "StatisticsRemoved";
    }
}

/// <summary>
/// Event payload for when a silo publishes its runtime statistics to the cluster.
/// </summary>
/// <param name="SiloAddress">The address of the silo publishing statistics.</param>
/// <param name="ActivationCount">The number of grain activations on this silo.</param>
/// <param name="RecentlyUsedActivationCount">The number of recently used grain activations.</param>
/// <param name="IsOverloaded">Whether the silo is currently overloaded.</param>
/// <param name="Timestamp">The timestamp when these statistics were collected.</param>
/// <param name="Statistics">The statistics instance.</param>
public class SiloStatisticsPublishedEvent(
    SiloAddress SiloAddress,
    int ActivationCount,
    int RecentlyUsedActivationCount,
    bool IsOverloaded,
    DateTime Timestamp,
    object Statistics)
{
    public SiloAddress SiloAddress { get; } = SiloAddress;
    public int ActivationCount { get; } = ActivationCount;
    public int RecentlyUsedActivationCount { get; } = RecentlyUsedActivationCount;
    public bool IsOverloaded { get; } = IsOverloaded;
    public DateTime Timestamp { get; } = Timestamp;
    public object Statistics { get; } = Statistics;
}

/// <summary>
/// Event payload for when a silo receives runtime statistics from another silo.
/// </summary>
/// <param name="FromSilo">The address of the silo that sent the statistics.</param>
/// <param name="ReceiverSilo">The address of the silo that received the statistics.</param>
/// <param name="ActivationCount">The number of grain activations on the sending silo.</param>
/// <param name="RecentlyUsedActivationCount">The number of recently used grain activations.</param>
/// <param name="IsOverloaded">Whether the sending silo is currently overloaded.</param>
/// <param name="Timestamp">The timestamp when these statistics were collected.</param>
/// <param name="Statistics">The statistics instance.</param>
public class SiloStatisticsReceivedEvent(
    SiloAddress FromSilo,
    SiloAddress ReceiverSilo,
    int ActivationCount,
    int RecentlyUsedActivationCount,
    bool IsOverloaded,
    DateTime Timestamp,
    object Statistics)
{
    public SiloAddress FromSilo { get; } = FromSilo;
    public SiloAddress ReceiverSilo { get; } = ReceiverSilo;
    public int ActivationCount { get; } = ActivationCount;
    public int RecentlyUsedActivationCount { get; } = RecentlyUsedActivationCount;
    public bool IsOverloaded { get; } = IsOverloaded;
    public DateTime Timestamp { get; } = Timestamp;
    public object Statistics { get; } = Statistics;
}

/// <summary>
/// Event payload for when a silo completes refreshing statistics from all cluster members.
/// </summary>
/// <param name="SiloAddress">The address of the silo that completed the refresh.</param>
/// <param name="SiloCount">The number of silos in the cluster.</param>
/// <param name="TotalActivationCount">The total number of activations across all silos.</param>
/// <param name="Statistics">The current cached statistics.</param>
public class ClusterStatisticsRefreshedEvent(
    SiloAddress SiloAddress,
    int SiloCount,
    int TotalActivationCount,
    object Statistics)
{
    public SiloAddress SiloAddress { get; } = SiloAddress;
    public int SiloCount { get; } = SiloCount;
    public int TotalActivationCount { get; } = TotalActivationCount;
    public object Statistics { get; } = Statistics;
}

/// <summary>
/// Event payload for when a silo's statistics are removed from the cache.
/// </summary>
/// <param name="RemovedSilo">The address of the silo whose statistics were removed.</param>
/// <param name="ObserverSilo">The address of the silo that removed the statistics.</param>
/// <param name="Reason">The reason for removal.</param>
public class SiloStatisticsRemovedEvent(
    SiloAddress RemovedSilo,
    SiloAddress ObserverSilo,
    string Reason)
{
    public SiloAddress RemovedSilo { get; } = RemovedSilo;
    public SiloAddress ObserverSilo { get; } = ObserverSilo;
    public string Reason { get; } = Reason;
}
