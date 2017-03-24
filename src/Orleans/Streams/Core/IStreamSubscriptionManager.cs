using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.Streams.Core
{
    public interface IStreamSubscriptionManager
    {
        Task<StreamSubscription> AddSubscription(string streamProviderName, IStreamIdentity streamId, GrainReference grainRef);
        Task RemoveSubscription(string streamProviderName, IStreamIdentity streamId, Guid subscriptionId);
        Task<IEnumerable<StreamSubscription>> GetSubscriptions(string streamProviderName, IStreamIdentity StreamId);
    }
}
