using System;
using System.Collections.Generic;
using System.Text;
using Orleans.Runtime;

namespace Orleans.Streams.Core
{
    public interface IStreamSubscriptionHandleFactory
    {
        StreamSubscriptionHandle<T> Create<T>(GuidId subscriptionId, IStreamIdentity streamId, string streamProviderName);
    }
}
