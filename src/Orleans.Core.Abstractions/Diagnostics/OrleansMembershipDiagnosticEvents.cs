using System;
using Orleans.Runtime;

namespace Orleans.Diagnostics;

/// <summary>
/// Provides diagnostic listener names and event names for Orleans membership events.
/// </summary>
/// <remarks>
/// These types are public but may change between minor versions. They are intended for
/// advanced scenarios such as simulation testing and diagnostics.
/// </remarks>
public static class OrleansMembershipDiagnostics
{
    /// <summary>
    /// The name of the diagnostic listener for membership events.
    /// </summary>
    public const string ListenerName = "Orleans.Membership";

    /// <summary>
    /// Event names for membership diagnostics.
    /// </summary>
    public static class EventNames
    {
        /// <summary>
        /// Event fired when a silo's status changes.
        /// Payload: <see cref="SiloStatusChangedEvent"/>
        /// </summary>
        public const string SiloStatusChanged = "SiloStatusChanged";

        /// <summary>
        /// Event fired when the membership view changes (any silo joins, leaves, or changes status).
        /// Payload: <see cref="MembershipViewChangedEvent"/>
        /// </summary>
        public const string ViewChanged = "ViewChanged";

        /// <summary>
        /// Event fired when a silo is suspected of being dead.
        /// Payload: <see cref="SiloSuspectedEvent"/>
        /// </summary>
        public const string SiloSuspected = "SiloSuspected";

        /// <summary>
        /// Event fired when a silo is declared dead.
        /// Payload: <see cref="SiloDeclaredDeadEvent"/>
        /// </summary>
        public const string SiloDeclaredDead = "SiloDeclaredDead";

        /// <summary>
        /// Event fired when a silo becomes active.
        /// Payload: <see cref="SiloBecameActiveEvent"/>
        /// </summary>
        public const string SiloBecameActive = "SiloBecameActive";

        /// <summary>
        /// Event fired when a silo begins joining the cluster.
        /// Payload: <see cref="SiloJoiningEvent"/>
        /// </summary>
        public const string SiloJoining = "SiloJoining";
    }
}

/// <summary>
/// Event payload for when a silo's status changes.
/// </summary>
/// <param name="SiloAddress">The address of the silo whose status changed.</param>
/// <param name="OldStatus">The previous status of the silo.</param>
/// <param name="NewStatus">The new status of the silo.</param>
/// <param name="ObserverSiloAddress">The address of the silo that observed this change.</param>
public class SiloStatusChangedEvent(
    SiloAddress SiloAddress,
    string OldStatus,
    string NewStatus,
    SiloAddress? ObserverSiloAddress)
{
    public SiloAddress SiloAddress { get; } = SiloAddress;
    public string OldStatus { get; } = OldStatus;
    public string NewStatus { get; } = NewStatus;
    public SiloAddress? ObserverSiloAddress { get; } = ObserverSiloAddress;
}

/// <summary>
/// Event payload for when the membership view changes.
/// </summary>
/// <param name="Version">The new membership table version.</param>
/// <param name="ActiveSiloCount">The number of active silos in the new view.</param>
/// <param name="TotalSiloCount">The total number of silos in the membership table.</param>
/// <param name="ObserverSiloAddress">The address of the silo that observed this change.</param>
public class MembershipViewChangedEvent(
    MembershipVersion Version,
    int ActiveSiloCount,
    int TotalSiloCount,
    SiloAddress? ObserverSiloAddress)
{
    public MembershipVersion Version { get; } = Version;
    public int ActiveSiloCount { get; } = ActiveSiloCount;
    public int TotalSiloCount { get; } = TotalSiloCount;
    public SiloAddress? ObserverSiloAddress { get; } = ObserverSiloAddress;
}

/// <summary>
/// Event payload for when a silo is suspected of being dead.
/// </summary>
/// <param name="SuspectedSilo">The address of the silo that is suspected.</param>
/// <param name="SuspectingSilo">The address of the silo that raised the suspicion.</param>
/// <param name="Reason">The reason for the suspicion.</param>
public class SiloSuspectedEvent(
    SiloAddress SuspectedSilo,
    SiloAddress SuspectingSilo,
    string Reason)
{
    public SiloAddress SuspectedSilo { get; } = SuspectedSilo;
    public SiloAddress SuspectingSilo { get; } = SuspectingSilo;
    public string Reason { get; } = Reason;
}

/// <summary>
/// Event payload for when a silo is declared dead.
/// </summary>
/// <param name="DeadSilo">The address of the silo that was declared dead.</param>
/// <param name="DeclaringSource">A description of what declared the silo dead.</param>
/// <param name="ObserverSiloAddress">The address of the silo that observed this declaration.</param>
public class SiloDeclaredDeadEvent(
    SiloAddress DeadSilo,
    string DeclaringSource,
    SiloAddress? ObserverSiloAddress)
{
    public SiloAddress DeadSilo { get; } = DeadSilo;
    public string DeclaringSource { get; } = DeclaringSource;
    public SiloAddress? ObserverSiloAddress { get; } = ObserverSiloAddress;
}

/// <summary>
/// Event payload for when a silo becomes active.
/// </summary>
/// <param name="SiloAddress">The address of the silo that became active.</param>
/// <param name="ObserverSiloAddress">The address of the silo that observed this event.</param>
public class SiloBecameActiveEvent(
    SiloAddress SiloAddress,
    SiloAddress? ObserverSiloAddress)
{
    public SiloAddress SiloAddress { get; } = SiloAddress;
    public SiloAddress? ObserverSiloAddress { get; } = ObserverSiloAddress;
}

/// <summary>
/// Event payload for when a silo begins joining the cluster.
/// </summary>
/// <param name="SiloAddress">The address of the silo that is joining.</param>
/// <param name="ObserverSiloAddress">The address of the silo that observed this event.</param>
public class SiloJoiningEvent(
    SiloAddress SiloAddress,
    SiloAddress? ObserverSiloAddress)
{
    public SiloAddress SiloAddress { get; } = SiloAddress;
    public SiloAddress? ObserverSiloAddress { get; } = ObserverSiloAddress;
}
