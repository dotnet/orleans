#nullable enable
using System;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.DurableTasks;

internal sealed class DurableTaskGrainRuntimeShared(
    IGrainContextAccessor grainContextAccessor,
    TimeProvider timeProvider,
    ILogger<DurableTaskGrainRuntime> logger)
{
    public IGrainContextAccessor GrainContextAccessor { get; } = grainContextAccessor;
    public TimeProvider TimeProvider { get; } = timeProvider;
    public ILogger<DurableTaskGrainRuntime> Logger { get; } = logger;
    public CleanupPolicy DefaultCleanupPolicy { get; } = new() { CleanupAge = TimeSpan.FromDays(1) };
}
