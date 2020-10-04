using Orleans.Runtime;
using System;
using Orleans.Streams.Core;

namespace Orleans.Streams
{
    public class StreamSubscriptionHandlerFactory : IStreamSubscriptionHandleFactory
    {
        public StreamId StreamId { get; }
        public string ProviderName { get; }
        public GuidId SubscriptionId { get; }
        private IStreamProvider streamProvider;

        public StreamSubscriptionHandlerFactory(IStreamProvider streamProvider, StreamId streamId, string providerName, GuidId subscriptionId)
        {
            this.streamProvider = streamProvider ?? throw new ArgumentNullException(nameof(streamProvider));
            this.StreamId = streamId;
            this.ProviderName = providerName;
            this.SubscriptionId = subscriptionId;
        }

        public StreamSubscriptionHandle<T> Create<T>()
        {
            var stream = this.streamProvider.GetStream<T>(StreamId) as StreamImpl<T>;
            return new StreamSubscriptionHandleImpl<T>(SubscriptionId, stream);
        }
    }
}
