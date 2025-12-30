using System;

namespace Orleans.Diagnostics;

/// <summary>
/// Provides diagnostic listener names and event names for Orleans grain activation events.
/// </summary>
/// <remarks>
/// These types are public but may change between minor versions. They are intended for
/// advanced scenarios such as simulation testing and diagnostics.
/// </remarks>
public static class OrleansGrainDiagnostics
{
    /// <summary>
    /// The name of the diagnostic listener for grain activation events.
    /// </summary>
    public const string ListenerName = "Orleans.Grains";

    /// <summary>
    /// Event names for grain diagnostics.
    /// </summary>
    public static class EventNames
    {
        /// <summary>
        /// Event fired when a grain activation is created.
        /// Payload: <see cref="GrainCreatedEvent"/>
        /// </summary>
        public const string Created = "Created";

        /// <summary>
        /// Event fired when a grain activation has completed activation.
        /// Payload: <see cref="GrainActivatedEvent"/>
        /// </summary>
        public const string Activated = "Activated";

        /// <summary>
        /// Event fired when a grain activation is about to deactivate.
        /// Payload: <see cref="GrainDeactivatingEvent"/>
        /// </summary>
        public const string Deactivating = "Deactivating";

        /// <summary>
        /// Event fired when a grain activation has completed deactivation.
        /// Payload: <see cref="GrainDeactivatedEvent"/>
        /// </summary>
        public const string Deactivated = "Deactivated";
    }
}

/// <summary>
/// Event payload for when a grain activation is created.
/// </summary>
/// <param name="GrainId">The grain ID.</param>
/// <param name="ActivationId">The activation ID.</param>
/// <param name="GrainType">The grain type name.</param>
/// <param name="SiloAddress">The address of the silo hosting this activation.</param>
public record GrainCreatedEvent(
    GrainId GrainId,
    ActivationId ActivationId,
    string GrainType,
    SiloAddress? SiloAddress);

/// <summary>
/// Event payload for when a grain activation has completed activation.
/// </summary>
/// <param name="GrainId">The grain ID.</param>
/// <param name="ActivationId">The activation ID.</param>
/// <param name="GrainType">The grain type name.</param>
/// <param name="SiloAddress">The address of the silo hosting this activation.</param>
/// <param name="Elapsed">The time taken to activate.</param>
public record GrainActivatedEvent(
    GrainId GrainId,
    ActivationId ActivationId,
    string GrainType,
    SiloAddress? SiloAddress,
    TimeSpan Elapsed);

/// <summary>
/// Event payload for when a grain activation is about to deactivate.
/// </summary>
/// <param name="GrainId">The grain ID.</param>
/// <param name="ActivationId">The activation ID.</param>
/// <param name="GrainType">The grain type name.</param>
/// <param name="SiloAddress">The address of the silo hosting this activation.</param>
/// <param name="ReasonCode">The reason code for deactivation.</param>
/// <param name="ReasonText">The reason text for deactivation.</param>
public record GrainDeactivatingEvent(
    GrainId GrainId,
    ActivationId ActivationId,
    string GrainType,
    SiloAddress? SiloAddress,
    DeactivationReasonCode ReasonCode,
    string? ReasonText);

/// <summary>
/// Event payload for when a grain activation has completed deactivation.
/// </summary>
/// <param name="GrainId">The grain ID.</param>
/// <param name="ActivationId">The activation ID.</param>
/// <param name="GrainType">The grain type name.</param>
/// <param name="SiloAddress">The address of the silo hosting this activation.</param>
/// <param name="Elapsed">The time taken to deactivate.</param>
public record GrainDeactivatedEvent(
    GrainId GrainId,
    ActivationId ActivationId,
    string GrainType,
    SiloAddress? SiloAddress,
    TimeSpan Elapsed);
