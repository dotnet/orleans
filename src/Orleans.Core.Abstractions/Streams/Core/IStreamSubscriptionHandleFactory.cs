using System;
using System.Collections.Generic;
using System.Text;
using Orleans.Runtime;

namespace Orleans.Streams.Core
{
    public interface IStreamSubscriptionHandleFactory
    {
        IStreamIdentity StreamId { get; }
        string ProviderName { get; }
        GuidId SubscriptionId { get; }
        StreamSubscriptionHandle<T> Create<T>();
    }
}
