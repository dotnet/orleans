using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.Streams.Core
{
    public interface IOnSubscriptionActioner
    {
        Task OnAdd<T>(StreamSubscriptionHandle<T> handle);
    }

    internal interface IOnSubscriptionActionInvoker
    {
        Task InvokeOnAdd(StreamId streamId, GuidId subscriptionId, IStreamProvider streamProvider);
    }

    internal class OnSubscriptionActionInvoker<T> : IOnSubscriptionActionInvoker
    {
        private readonly IOnSubscriptionActioner actioner;
        public OnSubscriptionActionInvoker(IOnSubscriptionActioner actioner)
        {
            this.actioner = actioner;
        }

        public Task InvokeOnAdd(StreamId streamId, GuidId subscriptionId, IStreamProvider streamProvider)
        {
            var stream = streamProvider.GetStream<T>(streamId.Guid, streamId.Namespace) as StreamImpl<T>;
            var handle = new StreamSubscriptionHandleImpl<T>(subscriptionId, stream);
            return this.actioner.OnAdd<T>(handle);
        }
    }
}
