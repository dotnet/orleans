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
        Task<StreamSubscription> AddSubscription(IStreamIdentity streamId, GrainReference grainRef);
        Task RemoveSubscription(IStreamIdentity streamId, Guid subscriptionId);
        Task<IEnumerable<StreamSubscription>> GetSubscriptions(IStreamIdentity StreamId);
    }
}
