using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.Streams.Core
{ 
    internal class SubscriptionChangeHandler<T> : ISubscriptionChangeHandler
    {
        public Func<StreamSubscriptionHandle<T>, Task> OnAdd { get; set; }

        public SubscriptionChangeHandler(Func<StreamSubscriptionHandle<T>, Task> onAddAction)
        {
            this.OnAdd = onAddAction;
        }

        public Task InvokeOnAdd(StreamId streamId, GuidId subscriptionId, bool isRewinable, IStreamProvider streamProvider)
        {
            var stream = streamProvider.GetStream<T>(streamId.Guid, streamId.Namespace) as StreamImpl<T>;
            var handler = new StreamSubscriptionHandleImpl<T>(subscriptionId, stream, isRewinable);
            return this.OnAdd.Invoke(handler);
        }
    }
}
