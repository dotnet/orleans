using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Streams.Core;

namespace Orleans.Streams
{
    /// <summary>
    /// Provider-facing interface for manager of streaming providers
    /// </summary>
    internal interface IStreamProviderRuntime : IProviderRuntime
    {
        /// <summary>
        /// Retrieves the opaque identity of currently executing grain or client object. 
        /// </summary>
        /// <remarks>Exposed for logging purposes.</remarks>
        string ExecutingEntityIdentity();

        /// <summary>
        /// Returns the stream directory.
        /// </summary>
        /// <returns>The stream directory.</returns>
        StreamDirectory GetStreamDirectory();

        void RegisterSystemTarget(ISystemTarget target);

        void UnregisterSystemTarget(ISystemTarget target);

        /// <summary>
        /// A Pub Sub runtime interface.
        /// </summary>
        /// <returns></returns>
        IStreamPubSub PubSub(StreamPubSubType pubSubType);

        /// <summary>A consistent ring interface.</summary>
        /// <param name="mySubRangeIndex">Index of the silo in the ring.</param>
        /// <param name="numSubRanges">Total number of sub ranges within this silo range.</param>
        /// <returns></returns>
        IConsistentRingProviderForGrains GetConsistentRingProvider(int mySubRangeIndex, int numSubRanges);
    }

    /// <summary>
    /// Provider-facing interface for manager of streaming providers
    /// </summary>
    internal interface ISiloSideStreamProviderRuntime : IStreamProviderRuntime
    {
        /// <summary>Start the pulling agents for a given persistent stream provider.</summary>
        Task<IPersistentStreamPullingManager> InitializePullingAgents(
            string streamProviderName,
            IQueueAdapterFactory adapterFactory,
            IQueueAdapter queueAdapter);
    }

    public enum StreamPubSubType
    {
        ExplicitGrainBasedAndImplicit,
        ExplicitGrainBasedOnly,
        ImplicitOnly,
    }

    internal interface IStreamPubSub // Compare with: IPubSubRendezvousGrain
    {
        Task<ISet<PubSubSubscriptionState>> RegisterProducer(StreamId streamId, string streamProvider, IStreamProducerExtension streamProducer);

        Task UnregisterProducer(StreamId streamId, string streamProvider, IStreamProducerExtension streamProducer);

        Task RegisterConsumer(GuidId subscriptionId, StreamId streamId, string streamProvider, IStreamConsumerExtension streamConsumer, IStreamFilterPredicateWrapper filter);

        Task UnregisterConsumer(GuidId subscriptionId, StreamId streamId, string streamProvider);

        Task<int> ProducerCount(Guid streamId, string streamProvider, string streamNamespace);

        Task<int> ConsumerCount(Guid streamId, string streamProvider, string streamNamespace);

        Task<List<StreamSubscription>> GetAllSubscriptions(StreamId streamId, IStreamConsumerExtension streamConsumer = null);

        GuidId CreateSubscriptionId(StreamId streamId, IStreamConsumerExtension streamConsumer);

        Task<bool> FaultSubscription(StreamId streamId, GuidId subscriptionId);
    }
}
