using System.Threading;
using System.Threading.Tasks;
using Orleans.Configuration;
using Orleans.Internal;
using Orleans.Runtime;
using Orleans.Streams.Filtering;

namespace Orleans.Streams;

internal sealed class PullingAgentTarget : SystemTarget, IPersistentStreamPullingAgent
{
    internal PersistentStreamPullingAgent Agent { get; }
    internal QueueId QueueId => Agent.QueueId;

    internal PullingAgentTarget(
        SystemTargetGrainId id,
        string providerName,
        IStreamPubSub pubSub,
        IStreamFilter streamFilter,
        QueueId queueId,
        StreamPullingAgentOptions options,
        IQueueAdapter queueAdapter,
        IQueueAdapterCache queueAdapterCache,
        IStreamFailureHandler streamFailureHandler,
        IBackoffProvider deliveryBackoffProvider,
        IBackoffProvider queueReaderBackoffProvider,
        System.TimeProvider timeProvider,
        SystemTargetShared shared,
        StreamInstruments? streamInstruments = null) : base(id, shared)
    {
        Agent = new(
            this, providerName, pubSub, streamFilter, queueId, options, queueAdapter, queueAdapterCache,
            streamFailureHandler, deliveryBackoffProvider, queueReaderBackoffProvider, timeProvider,
            shared.LoggerFactory, shared.TimerRegistry, shared.RuntimeClient?.InternalGrainFactory!, streamInstruments);
        shared.ActivationDirectory.RecordNewTarget(this);
    }

    public Task Initialize(CancellationToken cancellationToken) => Agent.Initialize(cancellationToken);
    public Task Shutdown(CancellationToken cancellationToken) => Agent.Shutdown(cancellationToken);

    public Task AddSubscriber(GuidId subscriptionId, QualifiedStreamId streamId, GrainId streamConsumer, string? filterData, CancellationToken cancellationToken)
        => Agent.AddSubscriber(subscriptionId, streamId, streamConsumer, filterData, cancellationToken);

    public Task RemoveSubscriber(GuidId subscriptionId, QualifiedStreamId streamId, CancellationToken cancellationToken)
        => Agent.RemoveSubscriber(subscriptionId, streamId, cancellationToken);
}
