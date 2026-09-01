using System.Collections.Generic;
using System.Threading;
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
            IQueueAdapter queueAdapter,
            CancellationToken cancellationToken);
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
        /// Supports implicit subscriptions declared on grain types and resolves them from grain metadata for minimum pub/sub control-plane overhead.
        /// Explicit subscriptions use <see cref="ExplicitGrainBasedAndImplicit"/> or <see cref="ExplicitGrainBasedOnly"/>.
        /// </summary>
        ImplicitOnly,
    }

    public interface IStreamPubSub // Compare with: IPubSubRendezvousGrain
    {
        Task<ISet<PubSubSubscriptionState>> RegisterProducer(QualifiedStreamId streamId, GrainId streamProducer);

        /// <summary>
        /// Registers a stream producer.
        /// </summary>
        /// <param name="streamId">The stream identifier.</param>
        /// <param name="streamProducer">The producer identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The set of stream subscriptions.</returns>
        Task<ISet<PubSubSubscriptionState>> RegisterProducer(QualifiedStreamId streamId, GrainId streamProducer, CancellationToken cancellationToken)
            => RegisterProducer(streamId, streamProducer);

        Task UnregisterProducer(QualifiedStreamId streamId, GrainId streamProducer);

        /// <summary>
        /// Unregisters a stream producer.
        /// </summary>
        /// <param name="streamId">The stream identifier.</param>
        /// <param name="streamProducer">The producer identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        Task UnregisterProducer(QualifiedStreamId streamId, GrainId streamProducer, CancellationToken cancellationToken)
            => UnregisterProducer(streamId, streamProducer);

        Task RegisterConsumer(GuidId subscriptionId, QualifiedStreamId streamId, GrainId streamConsumer, string? filterData);

        /// <summary>
        /// Registers a stream consumer.
        /// </summary>
        /// <param name="subscriptionId">The subscription identifier.</param>
        /// <param name="streamId">The stream identifier.</param>
        /// <param name="streamConsumer">The consumer identifier.</param>
        /// <param name="filterData">The optional filter data.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        Task RegisterConsumer(GuidId subscriptionId, QualifiedStreamId streamId, GrainId streamConsumer, string? filterData, CancellationToken cancellationToken)
            => RegisterConsumer(subscriptionId, streamId, streamConsumer, filterData);

        Task UnregisterConsumer(GuidId subscriptionId, QualifiedStreamId streamId);

        /// <summary>
        /// Unregisters a stream consumer.
        /// </summary>
        /// <param name="subscriptionId">The subscription identifier.</param>
        /// <param name="streamId">The stream identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
        Task UnregisterConsumer(GuidId subscriptionId, QualifiedStreamId streamId, CancellationToken cancellationToken)
            => UnregisterConsumer(subscriptionId, streamId);

        Task<int> ProducerCount(QualifiedStreamId streamId);

        /// <summary>
        /// Gets the number of producers registered for a stream.
        /// </summary>
        /// <param name="streamId">The stream identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The number of producers.</returns>
        Task<int> ProducerCount(QualifiedStreamId streamId, CancellationToken cancellationToken)
            => ProducerCount(streamId);

        Task<int> ConsumerCount(QualifiedStreamId streamId);

        /// <summary>
        /// Gets the number of consumers registered for a stream.
        /// </summary>
        /// <param name="streamId">The stream identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The number of consumers.</returns>
        Task<int> ConsumerCount(QualifiedStreamId streamId, CancellationToken cancellationToken)
            => ConsumerCount(streamId);

        Task<List<StreamSubscription>> GetAllSubscriptions(QualifiedStreamId streamId, GrainId streamConsumer = default);

        /// <summary>
        /// Gets the subscriptions for a stream.
        /// </summary>
        /// <param name="streamId">The stream identifier.</param>
        /// <param name="streamConsumer">The optional consumer identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The stream subscriptions.</returns>
        Task<List<StreamSubscription>> GetAllSubscriptions(QualifiedStreamId streamId, GrainId streamConsumer, CancellationToken cancellationToken)
            => GetAllSubscriptions(streamId, streamConsumer);

        GuidId CreateSubscriptionId(QualifiedStreamId streamId, GrainId streamConsumer);

        Task<bool> FaultSubscription(QualifiedStreamId streamId, GuidId subscriptionId);

        /// <summary>
        /// Marks a subscription as faulted.
        /// </summary>
        /// <param name="streamId">The stream identifier.</param>
        /// <param name="subscriptionId">The subscription identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns><see langword="true"/> if the subscription was faulted; otherwise, <see langword="false"/>.</returns>
        Task<bool> FaultSubscription(QualifiedStreamId streamId, GuidId subscriptionId, CancellationToken cancellationToken)
            => FaultSubscription(streamId, subscriptionId);
    }
}
