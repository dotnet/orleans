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

        /// <summary>
        /// A Pub Sub runtime interface.
        /// </summary>
        /// <returns></returns>
        IStreamPubSub PubSub(StreamPubSubType pubSubType);
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

    /// <summary>
    /// Identifies the publish/subscribe system types which stream providers can use.
    /// </summary>
    public enum StreamPubSubType
    {        
        /// <summary>
        /// Explicit and implicit pub/sub.
        /// </summary>
        ExplicitGrainBasedAndImplicit,

        /// <summary>
        /// Explicit pub/sub.
        /// </summary>
        ExplicitGrainBasedOnly,

        /// <summary>
        /// Implicit pub/sub.
        /// </summary>
        ImplicitOnly,
    }

    internal interface IStreamPubSub // Compare with: IPubSubRendezvousGrain
    {
        Task<ISet<PubSubSubscriptionState>> RegisterProducer(InternalStreamId streamId, IStreamProducerExtension streamProducer);

        Task UnregisterProducer(InternalStreamId streamId, IStreamProducerExtension streamProducer);

        Task RegisterConsumer(GuidId subscriptionId, InternalStreamId streamId, IStreamConsumerExtension streamConsumer, string filterData);

        Task UnregisterConsumer(GuidId subscriptionId, InternalStreamId streamId);

        Task<int> ProducerCount(InternalStreamId streamId);

        Task<int> ConsumerCount(InternalStreamId streamId);

        Task<List<StreamSubscription>> GetAllSubscriptions(InternalStreamId streamId, IStreamConsumerExtension streamConsumer = null);

        GuidId CreateSubscriptionId(InternalStreamId streamId, IStreamConsumerExtension streamConsumer);

        Task<bool> FaultSubscription(InternalStreamId streamId, GuidId subscriptionId);
    }
}
