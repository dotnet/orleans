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
        private IStreamProviderManager manager;
        public StreamSubscriptionHandlerFactory(IStreamProviderManager manager)
        {
            this.manager = manager;
        }

        public StreamSubscriptionHandle<T> Create<T>(GuidId subscriptionId, IStreamIdentity streamId, string providerName)
        {
            var streamProvider = this.manager.GetStreamProvider(providerName);
            var stream = streamProvider.GetStream<T>(streamId.Guid, streamId.Namespace) as StreamImpl<T>;
            return new StreamSubscriptionHandleImpl<T>(subscriptionId, stream);
        }
    }
}
