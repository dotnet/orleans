#nullable enable
using System;
using Microsoft.Extensions.Logging;
using Orleans.Runtime.Placement;

namespace Orleans.Runtime.DurableTasks;

internal sealed class DurableTaskGrainRuntimeShared(
    IGrainContextAccessor grainContextAccessor,
    TimeProvider timeProvider,
    PlacementStrategyResolver placementStrategyResolver,
    IGrainFactory grainFactory,
    ILogger<DurableTaskGrainRuntime> logger)
{
    public IGrainContextAccessor GrainContextAccessor { get; } = grainContextAccessor;
    public TimeProvider TimeProvider { get; } = timeProvider;
    public ILogger<DurableTaskGrainRuntime> Logger { get; } = logger;
    public PlacementStrategyResolver PlacementStrategyResolver { get; } = placementStrategyResolver;
    public IGrainFactory GrainFactory { get; } = grainFactory;
    public CleanupPolicy DefaultCleanupPolicy { get; } = new() { CleanupAge = TimeSpan.FromDays(1) };
}
