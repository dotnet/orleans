using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Messaging.Configuration;
using Orleans.Runtime;
using Orleans.Serialization;

namespace Orleans.DurableTasks.Runtime;

internal sealed class DurableTaskGrainRuntimeShared(
    IGrainContextAccessor grainContextAccessor,
    TimeProvider timeProvider,
    ILogger<DurableTaskGrainRuntime> logger,
    IOptions<DurableTaskOptions> options,
    IOptions<DurableMessagingOptions> messagingOptions,
    Serializer serializer)
{
    public IGrainContextAccessor GrainContextAccessor { get; } = grainContextAccessor;
    public TimeProvider TimeProvider { get; } = timeProvider;
    public ILogger<DurableTaskGrainRuntime> Logger { get; } = logger;
    public DurableTaskOptions Options { get; } = options.Value;

    /// <summary>
    /// Gets how long an outbox retries a message before dead-lettering it, which bounds how late an invocation can
    /// arrive after a cancellation for it.
    /// </summary>
    public TimeSpan MaxOutboxRetryAge { get; } = messagingOptions.Value.MaxOutboxRetryAge;

    public Serializer Serializer { get; } = serializer;
}