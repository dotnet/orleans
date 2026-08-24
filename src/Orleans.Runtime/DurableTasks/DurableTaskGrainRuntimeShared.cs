#nullable enable
using System;
using Microsoft.Extensions.Logging;
using Orleans.Serialization;

namespace Orleans.Runtime.DurableTasks;

internal sealed class DurableTaskGrainRuntimeShared(
    IGrainContextAccessor grainContextAccessor,
    TimeProvider timeProvider,
    ILogger<DurableTaskGrainRuntime> logger,
    Serializer serializer)
{
    public IGrainContextAccessor GrainContextAccessor { get; } = grainContextAccessor;
    public TimeProvider TimeProvider { get; } = timeProvider;
    public ILogger<DurableTaskGrainRuntime> Logger { get; } = logger;
    public Serializer Serializer { get; } = serializer;
    public CleanupPolicy DefaultCleanupPolicy { get; } = new() { CleanupAge = TimeSpan.FromDays(1) };
    internal TimeSpan DeactivationDrainTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
