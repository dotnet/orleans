using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Streams.Core
{
    public interface IStreamSubscriptionManager
    {
        Task<StreamSubscription> AddSubscription(string streamProviderName, StreamId streamId, GrainReference grainRef);
        Task RemoveSubscription(string streamProviderName, StreamId streamId, Guid subscriptionId);
        Task<IEnumerable<StreamSubscription>> GetSubscriptions(string streamProviderName, StreamId streamId);
    }
}
