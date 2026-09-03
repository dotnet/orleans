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

    /// <summary>
    /// Provides the stream provider pub/sub control plane for registering producers and consumers and querying subscription state.
    /// </summary>
    public interface IStreamPubSub // Compare with: IPubSubRendezvousGrain
    {
        /// <summary>
        /// Registers a producer for a stream and returns the active consumer registrations which the producer should connect to.
        /// </summary>
        /// <param name="streamId">The qualified stream identifier.</param>
        /// <param name="streamProducer">The identifier of the producer.</param>
        /// <returns>The active consumer registrations for the stream.</returns>
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

        /// <summary>
        /// Unregisters a producer from a stream.
        /// </summary>
        /// <param name="streamId">The qualified stream identifier.</param>
        /// <param name="streamProducer">The identifier of the producer.</param>
        /// <returns>A task which represents the operation.</returns>
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

        /// <summary>
        /// Registers a consumer subscription for a stream.
        /// </summary>
        /// <param name="subscriptionId">The subscription identifier.</param>
        /// <param name="streamId">The qualified stream identifier.</param>
        /// <param name="streamConsumer">The identifier of the consumer.</param>
        /// <param name="filterData">The serialized filter data associated with the subscription.</param>
        /// <returns>A task which represents the operation.</returns>
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

        /// <summary>
        /// Unregisters a consumer subscription from a stream.
        /// </summary>
        /// <param name="subscriptionId">The subscription identifier.</param>
        /// <param name="streamId">The qualified stream identifier.</param>
        /// <returns>A task which represents the operation.</returns>
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

        /// <summary>
        /// Gets the number of registered producers for a stream.
        /// </summary>
        /// <param name="streamId">The qualified stream identifier.</param>
        /// <returns>The number of registered producers.</returns>
        Task<int> ProducerCount(QualifiedStreamId streamId);

        /// <summary>
        /// Gets the number of producers registered for a stream.
        /// </summary>
        /// <param name="streamId">The stream identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The number of producers.</returns>
        Task<int> ProducerCount(QualifiedStreamId streamId, CancellationToken cancellationToken)
            => ProducerCount(streamId);

        /// <summary>
        /// Gets the number of registered consumers for a stream.
        /// </summary>
        /// <param name="streamId">The qualified stream identifier.</param>
        /// <returns>The number of registered consumers.</returns>
        Task<int> ConsumerCount(QualifiedStreamId streamId);

        /// <summary>
        /// Gets the number of consumers registered for a stream.
        /// </summary>
        /// <param name="streamId">The stream identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The number of consumers.</returns>
        Task<int> ConsumerCount(QualifiedStreamId streamId, CancellationToken cancellationToken)
            => ConsumerCount(streamId);

        /// <summary>
        /// Gets active subscriptions for a stream, optionally restricted to a specific consumer.
        /// </summary>
        /// <param name="streamId">The qualified stream identifier.</param>
        /// <param name="streamConsumer">
        /// The identifier of the consumer whose subscriptions are returned, or the default value to return all subscriptions.
        /// </param>
        /// <returns>The matching active subscriptions.</returns>
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

        /// <summary>
        /// Creates a subscription identifier for a stream consumer.
        /// </summary>
        /// <param name="streamId">The qualified stream identifier.</param>
        /// <param name="streamConsumer">The identifier of the consumer.</param>
        /// <returns>The subscription identifier.</returns>
        GuidId CreateSubscriptionId(QualifiedStreamId streamId, GrainId streamConsumer);

        /// <summary>
        /// Marks a subscription as faulted.
        /// </summary>
        /// <param name="streamId">The qualified stream identifier.</param>
        /// <param name="subscriptionId">The subscription identifier.</param>
        /// <returns><see langword="true"/> when the subscription is handled by this pub/sub implementation; otherwise, <see langword="false"/>.</returns>
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
