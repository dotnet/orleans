using Orleans.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.Concurrency;

namespace Orleans.Streams.Core
{
    [Serializable]
    [Immutable]
    public class StreamSubscription
    {
        public StreamSubscription(Guid subscriptionId, string streamProviderName, IStreamIdentity streamId, IGrainIdentity grainId)
        {
            this.SubscriptionId = subscriptionId;
            this.StreamProviderName = streamProviderName;
            this.StreamId = streamId;
            this.GrainId = grainId;
        }
        public Guid SubscriptionId { get; set; }
        public string StreamProviderName { get; set; }
        public IStreamIdentity StreamId { get; set; }
        public IGrainIdentity GrainId { get; set; }
    }
}
