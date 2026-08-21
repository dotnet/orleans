using System;
using System.Collections.Immutable;
using Orleans.Runtime;

namespace Orleans.Reminders.Concurrency;

/// <summary>
/// Context passed to <see cref="IReminderDeliveryThrottle.AcquireAsync"/> describing the
/// reminder tick that is about to be dispatched.
/// </summary>
public readonly struct ReminderDeliveryContext
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ReminderDeliveryContext"/> struct.
    /// </summary>
    /// <param name="grainId">The grain identity that owns the reminder.</param>
    /// <param name="reminderName">The reminder name.</param>
    /// <param name="status">The tick status that will be passed to the grain.</param>
    /// <param name="implementedInterfaces">
    /// The set of grain-interface types implemented by the target grain class. Used by
    /// per-grain-interface limiters. May be the default/empty value when interface
    /// resolution is not required by any registered throttle.
    /// </param>
    public ReminderDeliveryContext(
        GrainId grainId,
        string reminderName,
        TickStatus status,
        ImmutableArray<GrainInterfaceType> implementedInterfaces = default)
    {
        ArgumentNullException.ThrowIfNull(reminderName);
        GrainId = grainId;
        ReminderName = reminderName;
        Status = status;
        ImplementedInterfaces = implementedInterfaces.IsDefault ? ImmutableArray<GrainInterfaceType>.Empty : implementedInterfaces;
    }

    /// <summary>The grain identity that owns the reminder.</summary>
    public GrainId GrainId { get; }

    /// <summary>The grain class type. Convenience accessor for <see cref="GrainId"/>.Type.</summary>
    public GrainType GrainType => GrainId.Type;

    /// <summary>The reminder name.</summary>
    public string ReminderName { get; }

    /// <summary>The tick status that will be passed to the grain.</summary>
    public TickStatus Status { get; }

    /// <summary>The set of grain-interface types implemented by the target grain class.</summary>
    public ImmutableArray<GrainInterfaceType> ImplementedInterfaces { get; }

    /// <summary>The scheduled due time of this tick. Convenience accessor for <see cref="Status"/>.CurrentTickTime.</summary>
    public DateTime DueTime => Status.CurrentTickTime;
}
