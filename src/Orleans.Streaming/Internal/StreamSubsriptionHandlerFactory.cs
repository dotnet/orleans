using Orleans.Runtime;
using System;
using Orleans.Streams.Core;

namespace Orleans.Streams
{
    /// <summary>
    /// Factory for creating <see cref="StreamSubscriptionHandle{T}"/> instances.
    /// </summary>
    public class StreamSubscriptionHandlerFactory : IStreamSubscriptionHandleFactory
    {
        private readonly IStreamProvider streamProvider;
        
        /// <inheritdoc />
        public StreamId StreamId { get; }

        /// <inheritdoc />
        public string ProviderName { get; }

        /// <inheritdoc />
        public GuidId SubscriptionId { get; }
                        
        /// <summary>
        /// Initializes a new instance of the <see cref="StreamSubscriptionHandlerFactory"/> class.
        /// </summary>        
        /// <param name="streamProvider">
        /// The stream provider.
        /// </param>
        /// <param name="streamId">
        /// The stream identity.
        /// </param>
        /// <param name="providerName">
        /// The stream provider name.
        /// </param>
        /// <param name="subscriptionId">
        /// The subscription identity.
        /// </param>
        public StreamSubscriptionHandlerFactory(IStreamProvider streamProvider, StreamId streamId, string providerName, GuidId subscriptionId)
        {
            this.streamProvider = streamProvider ?? throw new ArgumentNullException(nameof(streamProvider));
            this.StreamId = streamId;
            this.ProviderName = providerName;
            this.SubscriptionId = subscriptionId;
        }

        /// <inheritdoc />
        public StreamSubscriptionHandle<T> Create<T>()
        {
            var stream = this.streamProvider.GetStream<T>(StreamId) as StreamImpl<T>;
            return new StreamSubscriptionHandleImpl<T>(SubscriptionId, stream);
        }
    }
}
