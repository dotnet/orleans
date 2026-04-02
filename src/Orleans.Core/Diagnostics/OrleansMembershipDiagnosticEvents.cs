#nullable enable
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
    }
}

/// <summary>
/// Event payload for when a silo's status changes.
/// </summary>
/// <param name="oldEntry">The previous membership entry for the silo, if one existed.</param>
/// <param name="newEntry">The new membership entry for the silo.</param>
/// <param name="observerSiloAddress">The address of the silo that observed this change.</param>
public class SiloStatusChangedEvent(
    MembershipEntry? oldEntry,
    MembershipEntry newEntry,
    SiloAddress? observerSiloAddress)
{
    public MembershipEntry? OldEntry { get; } = oldEntry;
    public MembershipEntry NewEntry { get; } = newEntry;
    public SiloAddress? ObserverSiloAddress { get; } = observerSiloAddress;
}

/// <summary>
/// Event payload for when the membership view changes.
/// </summary>
/// <param name="snapshot">The new membership snapshot.</param>
/// <param name="observerSiloAddress">The address of the silo that observed this change.</param>
public class MembershipViewChangedEvent(
    MembershipTableSnapshot snapshot,
    SiloAddress? observerSiloAddress)
{
    public MembershipTableSnapshot Snapshot { get; } = snapshot;
    public SiloAddress? ObserverSiloAddress { get; } = observerSiloAddress;
}

/// <summary>
/// Event payload for when a silo is suspected of being dead.
/// </summary>
/// <param name="suspectedSilo">The address of the silo that is suspected.</param>
/// <param name="suspectingSilo">The address of the silo that raised the suspicion.</param>
/// <param name="reason">The reason for the suspicion.</param>
public class SiloSuspectedEvent(
    SiloAddress suspectedSilo,
    SiloAddress suspectingSilo,
    string reason)
{
    public SiloAddress SuspectedSilo { get; } = suspectedSilo;
    public SiloAddress SuspectingSilo { get; } = suspectingSilo;
    public string Reason { get; } = reason;
}
