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
        IStreamPubSub? PubSub(StreamPubSubType pubSubType);
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
        /// Supports explicit subscriptions created at runtime and implicit subscriptions declared on grain types.
        /// </summary>
        ExplicitGrainBasedAndImplicit,

        /// <summary>
        /// Supports only explicit subscriptions created at runtime.
        /// </summary>
        ExplicitGrainBasedOnly,

        /// <summary>
        /// Supports only implicit subscriptions declared on grain types. Attempts to create explicit subscriptions will fail.
        /// </summary>
        ImplicitOnly,
    }

    public interface IStreamPubSub // Compare with: IPubSubRendezvousGrain
    {
        Task<ISet<PubSubSubscriptionState>> RegisterProducer(QualifiedStreamId streamId, GrainId streamProducer);

        Task UnregisterProducer(QualifiedStreamId streamId, GrainId streamProducer);

        Task RegisterConsumer(GuidId subscriptionId, QualifiedStreamId streamId, GrainId streamConsumer, string? filterData);

        Task UnregisterConsumer(GuidId subscriptionId, QualifiedStreamId streamId);

        Task<int> ProducerCount(QualifiedStreamId streamId);

        Task<int> ConsumerCount(QualifiedStreamId streamId);

        Task<List<StreamSubscription>> GetAllSubscriptions(QualifiedStreamId streamId, GrainId streamConsumer = default);

        GuidId CreateSubscriptionId(QualifiedStreamId streamId, GrainId streamConsumer);

        Task<bool> FaultSubscription(QualifiedStreamId streamId, GuidId subscriptionId);
    }
}
