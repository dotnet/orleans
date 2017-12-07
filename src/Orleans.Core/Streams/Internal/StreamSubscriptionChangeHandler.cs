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
    internal class StreamSubscriptionChangeHandler
    {
        private IStreamProviderManager providerManager;
        private Dictionary<Type, IStreamSubscriptionObserverProxy> subscriptionObserverMap;

        public StreamSubscriptionChangeHandler(IStreamProviderManager providerManager, Dictionary<Type, IStreamSubscriptionObserverProxy> observerMap)
        {
            this.providerManager = providerManager;
            this.subscriptionObserverMap = observerMap;
        }

        /// <summary>
        /// This method finds the correct IStreamSubscriptionObserver based on item's type, and invoke its OnNewSubscription method
        /// </summary>
        /// <param name="subscriptionId"></param>
        /// <param name="streamId"></param>
        /// <param name="messageType"></param>
        /// <returns></returns>
        public async Task HandleNewSubscription(GuidId subscriptionId, StreamId streamId, Type messageType)
        {
            // if no observer attached to the subscription, check if the grain is a IStreamSubscriptionObserver
            IStreamSubscriptionObserverProxy observerProxy;
            if (TryGetStreamSubscriptionObserverProxyForType(messageType, out observerProxy))
            {
                var streamProvider = this.providerManager.GetStreamProvider(streamId.ProviderName);

                await observerProxy.OnSubscribed(streamId, subscriptionId, streamProvider);
            }
        }

        private bool TryGetStreamSubscriptionObserverProxyForType(Type concretType, out IStreamSubscriptionObserverProxy observerProxy)
        {
            IStreamSubscriptionObserverProxy proxy = null;
            foreach (var observerEntry in this.subscriptionObserverMap)
            {
                //use GetTypeInfo for netstandard compatible
                if (observerEntry.Key.GetTypeInfo().IsAssignableFrom(concretType))
                {
                    proxy = observerEntry.Value;
                    break;
                }
            }
            observerProxy = proxy;
            return proxy != null;
        }
    }

    internal interface IStreamSubscriptionObserverProxy 
    {
        Task OnSubscribed(StreamId streamId, GuidId subscriptionId, IStreamProvider streamProvider);
        IAddressable SubscriptionObserver { set; }
    }

    /// <summary>
    /// Decorator class for a IStreamSubscriptionObserver. Created mainly to avoid using reflection to invoke
    /// methods on IStreamSubscriptionObserver.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    internal class StreamSubscriptionObserverProxy<T> : IStreamSubscriptionObserver<T>, IStreamSubscriptionObserverProxy
    {
        private IStreamSubscriptionObserver<T> observer;
        public IAddressable SubscriptionObserver
        {
            set
            {
                this.observer = (IStreamSubscriptionObserver<T>)value;
            }
        }

        public StreamSubscriptionObserverProxy()
        {
        }

        public Task OnSubscribed(StreamSubscriptionHandle<T> handle)
        {
            return this.observer.OnSubscribed(handle);
        }

        public Task OnSubscribed(StreamId streamId, GuidId subscriptionId, IStreamProvider streamProvider)
        {
            var stream = streamProvider.GetStream<T>(streamId.Guid, streamId.Namespace) as StreamImpl<T>;
            var handle = new StreamSubscriptionHandleImpl<T>(subscriptionId, stream);
            return this.OnSubscribed(handle);
        }
    }
}
