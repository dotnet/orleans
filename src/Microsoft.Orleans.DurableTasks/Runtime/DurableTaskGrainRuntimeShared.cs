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
    /// </summary>
    /// <remarks>
    /// Messaging requires <c>OutboxRetryTimeout</c> to be shorter, so a delivery which its retries complete is admitted.
    /// An invocation which backpressure or a delivery pipeline holds past the window is dead-lettered.
    /// </remarks>
    public TimeSpan InvocationAdmissionWindow { get; } = messagingOptions.Value.DeduplicationWindow;

    /// <summary>
    /// Gets how long after it arrives a cancellation or signal which precedes its invocation is recorded: twice
    /// <see cref="InvocationAdmissionWindow"/>, so an admitted invocation which its sender staged before that message finds
    /// it, whatever the skew between the sender's clock and this grain's.
    /// </summary>
    /// <remarks>
    /// Admission compares the invocation's send time, on the sender's clock, with this grain's clock, while the record's
    /// lifetime starts on this grain's clock. Messaging rejects a delivery whose send time is more than
    /// <c>DeduplicationWindow</c> ahead of the receiver's clock, so the record's message, which the sender staged no earlier
    /// than the invocation, was sent before its arrival plus one window, and the invocation is admitted only within one more
    /// window after that.
    /// </remarks>
    public TimeSpan EarlyRecordRetention { get; } = 2 * messagingOptions.Value.DeduplicationWindow;

    public Serializer Serializer { get; } = serializer;
}
