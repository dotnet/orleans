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
    /// Gets how long after its sender first staged it an invocation is admitted: Messaging's <c>DeduplicationWindow</c>.
    /// A cancellation or signal which arrives before its invocation is recorded for this long, so an admitted invocation
    /// finds it.
    /// </summary>
    /// <remarks>
    /// Messaging requires <c>OutboxRetryTimeout</c> to be shorter, so a delivery which its retries complete is admitted.
    /// An invocation which backpressure or a delivery pipeline holds past the window is dead-lettered.
    /// </remarks>
    public TimeSpan InvocationAdmissionWindow { get; } = messagingOptions.Value.DeduplicationWindow;

    public Serializer Serializer { get; } = serializer;
}
