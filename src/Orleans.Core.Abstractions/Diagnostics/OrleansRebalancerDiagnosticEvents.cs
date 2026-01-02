#nullable enable
using System;
using Orleans.Runtime;

namespace Orleans.Diagnostics;

/// <summary>
/// Provides diagnostic listener names and event names for Orleans activation rebalancer events.
/// </summary>
/// <remarks>
/// These types are public but may change between minor versions. They are intended for
/// advanced scenarios such as simulation testing and diagnostics.
/// </remarks>
public static class OrleansRebalancerDiagnostics
{
    /// <summary>
    /// The name of the diagnostic listener for activation rebalancer events.
    /// </summary>
    public const string ListenerName = "Orleans.Rebalancer";

    /// <summary>
    /// Event names for rebalancer diagnostics.
    /// </summary>
    public static class EventNames
    {
        /// <summary>
        /// Event fired when a rebalancing cycle starts.
        /// Payload: <see cref="RebalancerCycleStartEvent"/>
        /// </summary>
        public const string CycleStart = "CycleStart";

        /// <summary>
        /// Event fired when a rebalancing cycle completes.
        /// Payload: <see cref="RebalancerCycleStopEvent"/>
        /// </summary>
        public const string CycleStop = "CycleStop";

        /// <summary>
        /// Event fired when a rebalancing session starts.
        /// Payload: <see cref="RebalancerSessionStartEvent"/>
        /// </summary>
        public const string SessionStart = "SessionStart";

        /// <summary>
        /// Event fired when a rebalancing session stops.
        /// Payload: <see cref="RebalancerSessionStopEvent"/>
        /// </summary>
        public const string SessionStop = "SessionStop";
    }
}

/// <summary>
/// Event payload for when a rebalancing cycle starts.
/// </summary>
/// <param name="SiloAddress">The address of the silo hosting the rebalancer.</param>
/// <param name="CycleNumber">The cycle number within the current session.</param>
public record RebalancerCycleStartEvent(
    SiloAddress SiloAddress,
    int CycleNumber);

/// <summary>
/// Event payload for when a rebalancing cycle completes.
/// </summary>
/// <param name="SiloAddress">The address of the silo hosting the rebalancer.</param>
/// <param name="CycleNumber">The cycle number within the current session.</param>
/// <param name="ActivationsMigrated">The number of activations migrated during the cycle.</param>
/// <param name="EntropyDeviation">The entropy deviation after the cycle (0 = optimal balance, 1 = maximum imbalance).</param>
/// <param name="Elapsed">The time taken to complete the cycle.</param>
/// <param name="SessionCompleted">Whether this cycle resulted in session completion.</param>
public record RebalancerCycleStopEvent(
    SiloAddress SiloAddress,
    int CycleNumber,
    int ActivationsMigrated,
    double EntropyDeviation,
    TimeSpan Elapsed,
    bool SessionCompleted);

/// <summary>
/// Event payload for when a rebalancing session starts.
/// </summary>
/// <param name="SiloAddress">The address of the silo hosting the rebalancer.</param>
public record RebalancerSessionStartEvent(
    SiloAddress SiloAddress);

/// <summary>
/// Event payload for when a rebalancing session stops.
/// </summary>
/// <param name="SiloAddress">The address of the silo hosting the rebalancer.</param>
/// <param name="Reason">The reason the session stopped.</param>
/// <param name="TotalCycles">The total number of cycles completed in the session.</param>
public record RebalancerSessionStopEvent(
    SiloAddress SiloAddress,
    string Reason,
    int TotalCycles);
