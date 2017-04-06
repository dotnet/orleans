using Orleans.Runtime;
using System;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.Streams.Core;

namespace Orleans.Streams
{
    internal class StreamSubscriptionChangeHandler
    {
        private IStreamProviderRuntime providerRuntime;
        private Dictionary<Type, IStreamSubscriptionObserverProxy> subscriptionObserverMap;

        public StreamSubscriptionChangeHandler(IStreamProviderRuntime providerRT, Dictionary<Type, IStreamSubscriptionObserverProxy> observerMap)
        {
            this.providerRuntime = providerRT;
            this.subscriptionObserverMap = observerMap;
        }

        /// <summary>
        /// This method finds the correct IStreamSubscriptionObserver<T> based on item's type, and invoke its OnNewSubscription method
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
                var streamProvider = this.providerRuntime.ServiceProvider
                            .GetService<IStreamProviderManager>()
                            .GetStreamProvider(streamId.ProviderName);

                await observerProxy.InvokeOnNewSubscription(streamId, subscriptionId, streamProvider);
            }
        }

        private bool TryGetStreamSubscriptionObserverProxyForType(Type concretType, out IStreamSubscriptionObserverProxy observerProxy)
        {
            IStreamSubscriptionObserverProxy proxy = null;
            foreach (var observerEntry in this.subscriptionObserverMap)
            {
                if (observerEntry.Key.IsAssignableFrom(concretType))
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
        Task InvokeOnNewSubscription(StreamId streamId, GuidId subscriptionId, IStreamProvider streamProvider);
        IAddressable SubscriptionObserver { set; }
    }

    /// <summary>
    /// Decorator class for a IStreamSubscriptionObserver<T>. Created mainly to avoid using reflection to invoke
    /// methods on IStreamSubscriptionObserver<T>.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    internal class StreamSubscriptionObserverProxy<T> : IStreamSubscriptionObserver<T>, IStreamSubscriptionObserverProxy
    {
        private IStreamSubscriptionObserver<T> observer => this.SubscriptionObserver as IStreamSubscriptionObserver<T>;
        public IAddressable SubscriptionObserver { private get; set; }
        public StreamSubscriptionObserverProxy()
        {
        }

        public Task OnNewSubscription(StreamSubscriptionHandle<T> handle)
        {
            return this.observer.OnNewSubscription(handle);
        }

        public Task InvokeOnNewSubscription(StreamId streamId, GuidId subscriptionId, IStreamProvider streamProvider)
        {
            var stream = streamProvider.GetStream<T>(streamId.Guid, streamId.Namespace) as StreamImpl<T>;
            var handle = new StreamSubscriptionHandleImpl<T>(subscriptionId, stream);
            return this.observer.OnNewSubscription(handle);
        }
    }
}
