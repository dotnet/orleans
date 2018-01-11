using Orleans.Runtime;
using System;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.Streams.Core;
using System.Reflection;

namespace Orleans.Streams
{
    public class StreamSubscriptionHandlerFactory : IStreamSubscriptionHandleFactory
    {
        public IStreamIdentity StreamId { get; }
        public string ProviderName { get; }
        public GuidId SubscriptionId { get; }
        private IStreamProviderManager manager;
        public StreamSubscriptionHandlerFactory(IStreamProviderManager manager, IStreamIdentity streamId, string providerName, GuidId subscriptionId)
        {
            this.manager = manager;
            this.StreamId = streamId;
            this.ProviderName = providerName;
            this.SubscriptionId = subscriptionId;
        }

        public StreamSubscriptionHandle<T> Create<T>()
        {
            var streamProvider = this.manager.GetStreamProvider(ProviderName);
            var stream = streamProvider.GetStream<T>(StreamId.Guid, StreamId.Namespace) as StreamImpl<T>;
            return new StreamSubscriptionHandleImpl<T>(SubscriptionId, stream);
        }
    }
}
