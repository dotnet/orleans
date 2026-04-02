using System;
using System.Diagnostics;
using Orleans.Runtime;

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
/// <param name="GrainContext">The grain context.</param>
public class GrainCreatedEvent(
    GrainId GrainId,
    ActivationId ActivationId,
    string GrainType,
    SiloAddress? SiloAddress,
    IGrainContext GrainContext)
{
    public GrainId GrainId { get; } = GrainId;
    public ActivationId ActivationId { get; } = ActivationId;
    public string GrainType { get; } = GrainType;
    public SiloAddress? SiloAddress { get; } = SiloAddress;
    public IGrainContext GrainContext { get; } = GrainContext;
}

/// <summary>
/// Event payload for when a grain activation has completed activation.
/// </summary>
/// <param name="GrainId">The grain ID.</param>
/// <param name="ActivationId">The activation ID.</param>
/// <param name="GrainType">The grain type name.</param>
/// <param name="SiloAddress">The address of the silo hosting this activation.</param>
/// <param name="Elapsed">The time taken to activate.</param>
/// <param name="GrainContext">The grain context.</param>
public class GrainActivatedEvent(
    GrainId GrainId,
    ActivationId ActivationId,
    string GrainType,
    SiloAddress? SiloAddress,
    TimeSpan Elapsed,
    IGrainContext GrainContext)
{
    public GrainId GrainId { get; } = GrainId;
    public ActivationId ActivationId { get; } = ActivationId;
    public string GrainType { get; } = GrainType;
    public SiloAddress? SiloAddress { get; } = SiloAddress;
    public TimeSpan Elapsed { get; } = Elapsed;
    public IGrainContext GrainContext { get; } = GrainContext;
}

/// <summary>
/// Event payload for when a grain activation is about to deactivate.
/// </summary>
/// <param name="GrainId">The grain ID.</param>
/// <param name="ActivationId">The activation ID.</param>
/// <param name="GrainType">The grain type name.</param>
/// <param name="SiloAddress">The address of the silo hosting this activation.</param>
/// <param name="ReasonCode">The reason code for deactivation.</param>
/// <param name="ReasonText">The reason text for deactivation.</param>
/// <param name="GrainContext">The grain context.</param>
public class GrainDeactivatingEvent(
    GrainId GrainId,
    ActivationId ActivationId,
    string GrainType,
    SiloAddress? SiloAddress,
    DeactivationReasonCode ReasonCode,
    string? ReasonText,
    IGrainContext GrainContext)
{
    public GrainId GrainId { get; } = GrainId;
    public ActivationId ActivationId { get; } = ActivationId;
    public string GrainType { get; } = GrainType;
    public SiloAddress? SiloAddress { get; } = SiloAddress;
    public DeactivationReasonCode ReasonCode { get; } = ReasonCode;
    public string? ReasonText { get; } = ReasonText;
    public IGrainContext GrainContext { get; } = GrainContext;
}

/// <summary>
/// Event payload for when a grain activation has completed deactivation.
/// </summary>
/// <param name="GrainId">The grain ID.</param>
/// <param name="ActivationId">The activation ID.</param>
/// <param name="GrainType">The grain type name.</param>
/// <param name="SiloAddress">The address of the silo hosting this activation.</param>
/// <param name="Elapsed">The time taken to deactivate.</param>
/// <param name="GrainContext">The grain context.</param>
public class GrainDeactivatedEvent(
    GrainId GrainId,
    ActivationId ActivationId,
    string GrainType,
    SiloAddress? SiloAddress,
    TimeSpan Elapsed,
    IGrainContext GrainContext)
{
    public GrainId GrainId { get; } = GrainId;
    public ActivationId ActivationId { get; } = ActivationId;
    public string GrainType { get; } = GrainType;
    public SiloAddress? SiloAddress { get; } = SiloAddress;
    public TimeSpan Elapsed { get; } = Elapsed;
    public IGrainContext GrainContext { get; } = GrainContext;
}

internal static class OrleansGrainDiagnosticListener
{
    private static readonly DiagnosticListener Listener = new(OrleansGrainDiagnostics.ListenerName);

    internal static void EmitActivated(IGrainContext grainContext, string grainType, SiloAddress? siloAddress, TimeSpan elapsed)
    {
        if (!Listener.IsEnabled(OrleansGrainDiagnostics.EventNames.Activated))
        {
            return;
        }

        Emit(Listener, grainContext, grainType, siloAddress, elapsed);

        static void Emit(DiagnosticListener listener, IGrainContext grainContext, string grainType, SiloAddress? siloAddress, TimeSpan elapsed)
        {
            listener.Write(OrleansGrainDiagnostics.EventNames.Activated, new GrainActivatedEvent(
                grainContext.GrainId,
                grainContext.ActivationId,
                grainType,
                siloAddress,
                elapsed,
                grainContext));
        }
    }

    internal static void EmitCreated(IGrainContext grainContext, string grainType, SiloAddress? siloAddress)
    {
        if (!Listener.IsEnabled(OrleansGrainDiagnostics.EventNames.Created))
        {
            return;
        }

        Emit(Listener, grainContext, grainType, siloAddress);

        static void Emit(DiagnosticListener listener, IGrainContext grainContext, string grainType, SiloAddress? siloAddress)
        {
            listener.Write(OrleansGrainDiagnostics.EventNames.Created, new GrainCreatedEvent(
                grainContext.GrainId,
                grainContext.ActivationId,
                grainType,
                siloAddress,
                grainContext));
        }
    }

    internal static void EmitDeactivated(IGrainContext grainContext, string grainType, SiloAddress? siloAddress, TimeSpan elapsed)
    {
        if (!Listener.IsEnabled(OrleansGrainDiagnostics.EventNames.Deactivated))
        {
            return;
        }

        Emit(Listener, grainContext, grainType, siloAddress, elapsed);

        static void Emit(DiagnosticListener listener, IGrainContext grainContext, string grainType, SiloAddress? siloAddress, TimeSpan elapsed)
        {
            listener.Write(OrleansGrainDiagnostics.EventNames.Deactivated, new GrainDeactivatedEvent(
                grainContext.GrainId,
                grainContext.ActivationId,
                grainType,
                siloAddress,
                elapsed,
                grainContext));
        }
    }

    internal static void EmitDeactivating(IGrainContext grainContext, string grainType, SiloAddress? siloAddress, DeactivationReason deactivationReason)
    {
        if (!Listener.IsEnabled(OrleansGrainDiagnostics.EventNames.Deactivating))
        {
            return;
        }

        Emit(Listener, grainContext, grainType, siloAddress, deactivationReason);

        static void Emit(DiagnosticListener listener, IGrainContext grainContext, string grainType, SiloAddress? siloAddress, DeactivationReason deactivationReason)
        {
            listener.Write(OrleansGrainDiagnostics.EventNames.Deactivating, new GrainDeactivatingEvent(
                grainContext.GrainId,
                grainContext.ActivationId,
                grainType,
                siloAddress,
                deactivationReason.ReasonCode,
                deactivationReason.Description,
                grainContext));
        }
    }
}
