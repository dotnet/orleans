#nullable enable
using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Serialization;

namespace Orleans.DurableTasks.Runtime;

internal sealed class DurableTaskGrainRuntimeShared(
    IGrainContextAccessor grainContextAccessor,
    TimeProvider timeProvider,
    ILogger<DurableTaskGrainRuntime> logger,
    IOptions<DurableTaskOptions> options,
    Serializer serializer)
{
    public IGrainContextAccessor GrainContextAccessor { get; } = grainContextAccessor;
    public TimeProvider TimeProvider { get; } = timeProvider;
    public ILogger<DurableTaskGrainRuntime> Logger { get; } = logger;
    public DurableTaskOptions Options { get; } = options.Value;
    public Serializer Serializer { get; } = serializer;
}
