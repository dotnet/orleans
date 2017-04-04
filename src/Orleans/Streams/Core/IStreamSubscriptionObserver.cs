using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.Streams.Core
{
    public interface IStreamSubscriptionObserver<T>
    {
        Task OnNewSubscription(StreamSubscriptionHandle<T> handle);
    }

    internal interface IOnSubscriptionActionInvoker
    {
        Task InvokeOnNewSubscription(StreamId streamId, GuidId subscriptionId, IStreamProvider streamProvider);
    }

    internal class OnSubscriptionActionInvoker<T> : IOnSubscriptionActionInvoker
    {
        private readonly IStreamSubscriptionObserver<T> observer;
        public OnSubscriptionActionInvoker(IStreamSubscriptionObserver<T> observer)
        {
            this.observer = observer;
        }

        public Task InvokeOnNewSubscription(StreamId streamId, GuidId subscriptionId, IStreamProvider streamProvider)
        {
            var stream = streamProvider.GetStream<T>(streamId.Guid, streamId.Namespace) as StreamImpl<T>;
            var handle = new StreamSubscriptionHandleImpl<T>(subscriptionId, stream);
            return this.observer.OnNewSubscription(handle);
        }
    }
}
