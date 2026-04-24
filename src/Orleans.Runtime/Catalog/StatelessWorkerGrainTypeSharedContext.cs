using System;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Runtime;

/// <summary>
/// Functionality which is shared between all instances of a stateless worker grain type.
/// </summary>
internal sealed class StatelessWorkerGrainTypeSharedContext
{
    public StatelessWorkerGrainTypeSharedContext(
        GrainTypeSharedContext shared,
        IOptions<StatelessWorkerOptions> statelessWorkerOptions)
    {
        Shared = shared;
        StatelessWorkerOptions = statelessWorkerOptions.Value;

        var placement = (StatelessWorkerPlacement)shared.PlacementStrategy;
        MaxLocalWorkers = placement.MaxLocal;
        RemoveIdleWorkers = placement.RemoveIdleWorkers && StatelessWorkerOptions.RemoveIdleWorkers;
        MinIdleCyclesBeforeRemoval = StatelessWorkerOptions.MinIdleCyclesBeforeRemoval > 0
            ? StatelessWorkerOptions.MinIdleCyclesBeforeRemoval
            : 1;
        IdleWorkersInspectionPeriod = StatelessWorkerOptions.IdleWorkersInspectionPeriod;
    }

    /// <summary>
    /// Gets the general grain-type-wide shared context.
    /// </summary>
    public GrainTypeSharedContext Shared { get; }

    /// <summary>
    /// Gets the stateless worker options.
    /// </summary>
    public StatelessWorkerOptions StatelessWorkerOptions { get; }

    /// <summary>
    /// Gets the maximum number of local worker activations permitted for a grain id of this type.
    /// </summary>
    public int MaxLocalWorkers { get; }

    /// <summary>
    /// Gets a value indicating whether idle workers should be proactively removed.
    /// </summary>
    public bool RemoveIdleWorkers { get; }

    /// <summary>
    /// Gets the minimum number of consecutive idle inspection cycles required before a worker may be removed.
    /// </summary>
    public int MinIdleCyclesBeforeRemoval { get; }

    /// <summary>
    /// Gets the period between idle worker inspections.
    /// </summary>
    public TimeSpan IdleWorkersInspectionPeriod { get; }
}
