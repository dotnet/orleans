#nullable enable
using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Runtime.DurableTasks;

internal sealed class DurableTaskGrainRuntimeShared(
    IGrainContextAccessor grainContextAccessor,
    TimeProvider timeProvider,
    ILogger<DurableTaskGrainRuntime> logger,
    IOptions<DurableTaskOptions> options)
{
    public IGrainContextAccessor GrainContextAccessor { get; } = grainContextAccessor;
    public TimeProvider TimeProvider { get; } = timeProvider;
    public ILogger<DurableTaskGrainRuntime> Logger { get; } = logger;
    public DurableTaskOptions Options { get; } = options.Value;
}
