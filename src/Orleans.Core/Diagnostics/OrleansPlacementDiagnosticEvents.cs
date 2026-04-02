#nullable enable
using System;
using System.Collections.Generic;
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
/// <param name="siloAddress">The address of the silo publishing statistics.</param>
/// <param name="statistics">The published runtime statistics.</param>
public class SiloStatisticsPublishedEvent(
    SiloAddress siloAddress,
    SiloRuntimeStatistics statistics)
{
    public SiloAddress SiloAddress { get; } = siloAddress;
    public SiloRuntimeStatistics Statistics { get; } = statistics;
}

/// <summary>
/// Event payload for when a silo receives runtime statistics from another silo.
/// </summary>
/// <param name="fromSilo">The address of the silo that sent the statistics.</param>
/// <param name="receiverSilo">The address of the silo that received the statistics.</param>
/// <param name="statistics">The received runtime statistics.</param>
public class SiloStatisticsReceivedEvent(
    SiloAddress fromSilo,
    SiloAddress receiverSilo,
    SiloRuntimeStatistics statistics)
{
    public SiloAddress FromSilo { get; } = fromSilo;
    public SiloAddress ReceiverSilo { get; } = receiverSilo;
    public SiloRuntimeStatistics Statistics { get; } = statistics;
}

/// <summary>
/// Event payload for when a silo completes refreshing statistics from all cluster members.
/// </summary>
/// <param name="siloAddress">The address of the silo that completed the refresh.</param>
/// <param name="statistics">The current cached cluster statistics.</param>
public class ClusterStatisticsRefreshedEvent(
    SiloAddress siloAddress,
    IReadOnlyDictionary<SiloAddress, SiloRuntimeStatistics> statistics)
{
    public SiloAddress SiloAddress { get; } = siloAddress;
    public IReadOnlyDictionary<SiloAddress, SiloRuntimeStatistics> Statistics { get; } = statistics;
}

/// <summary>
/// Event payload for when a silo's statistics are removed from the cache.
/// </summary>
/// <param name="removedSilo">The address of the silo whose statistics were removed.</param>
/// <param name="observerSilo">The address of the silo that removed the statistics.</param>
public class SiloStatisticsRemovedEvent(
    SiloAddress removedSilo,
    SiloAddress observerSilo)
{
    public SiloAddress RemovedSilo { get; } = removedSilo;
    public SiloAddress ObserverSilo { get; } = observerSilo;
}
