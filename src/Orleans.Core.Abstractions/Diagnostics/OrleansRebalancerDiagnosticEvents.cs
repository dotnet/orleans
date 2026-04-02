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
public class RebalancerCycleStartEvent(
    SiloAddress SiloAddress,
    int CycleNumber)
{
    public SiloAddress SiloAddress { get; } = SiloAddress;
    public int CycleNumber { get; } = CycleNumber;
}

/// <summary>
/// Event payload for when a rebalancing cycle completes.
/// </summary>
/// <param name="SiloAddress">The address of the silo hosting the rebalancer.</param>
/// <param name="CycleNumber">The cycle number within the current session.</param>
/// <param name="ActivationsMigrated">The number of activations migrated during the cycle.</param>
/// <param name="EntropyDeviation">The entropy deviation after the cycle.</param>
/// <param name="Elapsed">The time taken to complete the cycle.</param>
/// <param name="SessionCompleted">Whether this cycle resulted in session completion.</param>
public class RebalancerCycleStopEvent(
    SiloAddress SiloAddress,
    int CycleNumber,
    int ActivationsMigrated,
    double EntropyDeviation,
    TimeSpan Elapsed,
    bool SessionCompleted)
{
    public SiloAddress SiloAddress { get; } = SiloAddress;
    public int CycleNumber { get; } = CycleNumber;
    public int ActivationsMigrated { get; } = ActivationsMigrated;
    public double EntropyDeviation { get; } = EntropyDeviation;
    public TimeSpan Elapsed { get; } = Elapsed;
    public bool SessionCompleted { get; } = SessionCompleted;
}

/// <summary>
/// Event payload for when a rebalancing session starts.
/// </summary>
/// <param name="SiloAddress">The address of the silo hosting the rebalancer.</param>
public class RebalancerSessionStartEvent(
    SiloAddress SiloAddress)
{
    public SiloAddress SiloAddress { get; } = SiloAddress;
}

/// <summary>
/// Event payload for when a rebalancing session stops.
/// </summary>
/// <param name="SiloAddress">The address of the silo hosting the rebalancer.</param>
/// <param name="Reason">The reason the session stopped.</param>
/// <param name="TotalCycles">The total number of cycles completed in the session.</param>
public class RebalancerSessionStopEvent(
    SiloAddress SiloAddress,
    string Reason,
    int TotalCycles)
{
    public SiloAddress SiloAddress { get; } = SiloAddress;
    public string Reason { get; } = Reason;
    public int TotalCycles { get; } = TotalCycles;
}
