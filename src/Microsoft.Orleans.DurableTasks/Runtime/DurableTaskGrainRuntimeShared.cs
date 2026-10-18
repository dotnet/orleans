using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.DurableJobs;
using Orleans.Messaging.Configuration;
using Orleans.Runtime;
using Orleans.Serialization;

namespace Orleans.DurableTasks.Runtime;

internal sealed class DurableTaskGrainRuntimeShared(
    IGrainContextAccessor grainContextAccessor,
    [FromKeyedServices(DurableJobTimeProviderNames.DurableJobs)] TimeProvider timeProvider,
    ILogger<DurableTaskGrainRuntime> logger,
    IOptions<DurableTaskOptions> options,
    IOptions<DurableMessagingOptions> messagingOptions,
    Serializer serializer)
{
    public IGrainContextAccessor GrainContextAccessor { get; } = grainContextAccessor;
    /// <summary>
    /// Gets the Durable Jobs clock, which Messaging compares <c>Envelope.DeliverAt</c> and outbox retry times against. Logical
    /// time, delay due times, and task timestamps use it, so they share one time domain with message scheduling.
    /// </summary>
    public TimeProvider TimeProvider { get; } = timeProvider;
    public ILogger<DurableTaskGrainRuntime> Logger { get; } = logger;
    public DurableTaskOptions Options { get; } = options.Value;

    /// <summary>
    /// Gets how long an outbox retries a message before dead-lettering it, which bounds how late an invocation can
    /// arrive after a cancellation for it.
    /// </summary>
    public TimeSpan OutboxRetryTimeout { get; } = messagingOptions.Value.OutboxRetryTimeout;

    public Serializer Serializer { get; } = serializer;
}
