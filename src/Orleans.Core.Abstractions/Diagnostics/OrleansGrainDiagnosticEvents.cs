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
/// <param name="GrainContext">The grain context.</param>
public class GrainCreatedEvent(IGrainContext GrainContext)
{
    public IGrainContext GrainContext { get; } = GrainContext;
}

/// <summary>
/// Event payload for when a grain activation has completed activation.
/// </summary>
/// <param name="GrainContext">The grain context.</param>
/// <param name="Elapsed">The time taken to activate.</param>
public class GrainActivatedEvent(
    IGrainContext GrainContext,
    TimeSpan Elapsed)
{
    public IGrainContext GrainContext { get; } = GrainContext;
    public TimeSpan Elapsed { get; } = Elapsed;
}

/// <summary>
/// Event payload for when a grain activation is about to deactivate.
/// </summary>
/// <param name="GrainContext">The grain context.</param>
/// <param name="ReasonCode">The reason code for deactivation.</param>
/// <param name="ReasonText">The reason text for deactivation.</param>
public class GrainDeactivatingEvent(
    IGrainContext GrainContext,
    DeactivationReasonCode ReasonCode,
    string? ReasonText)
{
    public IGrainContext GrainContext { get; } = GrainContext;
    public DeactivationReasonCode ReasonCode { get; } = ReasonCode;
    public string? ReasonText { get; } = ReasonText;
}

/// <summary>
/// Event payload for when a grain activation has completed deactivation.
/// </summary>
/// <param name="GrainContext">The grain context.</param>
/// <param name="Elapsed">The time taken to deactivate.</param>
public class GrainDeactivatedEvent(
    IGrainContext GrainContext,
    TimeSpan Elapsed)
{
    public IGrainContext GrainContext { get; } = GrainContext;
    public TimeSpan Elapsed { get; } = Elapsed;
}

internal static class OrleansGrainDiagnosticListener
{
    private static readonly DiagnosticListener Listener = new(OrleansGrainDiagnostics.ListenerName);

    internal static void EmitActivated(IGrainContext grainContext, TimeSpan elapsed)
    {
        if (!Listener.IsEnabled(OrleansGrainDiagnostics.EventNames.Activated))
        {
            return;
        }

        Emit(Listener, grainContext, elapsed);

        static void Emit(DiagnosticListener listener, IGrainContext grainContext, TimeSpan elapsed)
        {
            listener.Write(OrleansGrainDiagnostics.EventNames.Activated, new GrainActivatedEvent(
                grainContext,
                elapsed));
        }
    }

    internal static void EmitCreated(IGrainContext grainContext)
    {
        if (!Listener.IsEnabled(OrleansGrainDiagnostics.EventNames.Created))
        {
            return;
        }

        Emit(Listener, grainContext);

        static void Emit(DiagnosticListener listener, IGrainContext grainContext)
        {
            listener.Write(OrleansGrainDiagnostics.EventNames.Created, new GrainCreatedEvent(grainContext));
        }
    }

    internal static void EmitDeactivated(IGrainContext grainContext, TimeSpan elapsed)
    {
        if (!Listener.IsEnabled(OrleansGrainDiagnostics.EventNames.Deactivated))
        {
            return;
        }

        Emit(Listener, grainContext, elapsed);

        static void Emit(DiagnosticListener listener, IGrainContext grainContext, TimeSpan elapsed)
        {
            listener.Write(OrleansGrainDiagnostics.EventNames.Deactivated, new GrainDeactivatedEvent(
                grainContext,
                elapsed));
        }
    }

    internal static void EmitDeactivating(IGrainContext grainContext, DeactivationReason deactivationReason)
    {
        if (!Listener.IsEnabled(OrleansGrainDiagnostics.EventNames.Deactivating))
        {
            return;
        }

        Emit(Listener, grainContext, deactivationReason);

        static void Emit(DiagnosticListener listener, IGrainContext grainContext, DeactivationReason deactivationReason)
        {
            listener.Write(OrleansGrainDiagnostics.EventNames.Deactivating, new GrainDeactivatingEvent(
                grainContext,
                deactivationReason.ReasonCode,
                deactivationReason.Description));
        }
    }
}
