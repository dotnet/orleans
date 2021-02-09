using System;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace Orleans.Streams.Core
{
    [Serializable]
    [Immutable]
    public class StreamSubscription
    {
        public StreamSubscription(Guid subscriptionId, string streamProviderName, StreamId streamId, GrainId grainId)
        {
            this.SubscriptionId = subscriptionId;
            this.StreamProviderName = streamProviderName;
            this.StreamId = streamId;
            this.GrainId = grainId;
        }
        public Guid SubscriptionId { get; set; }
        public string StreamProviderName { get; set; }
        public StreamId StreamId { get; set; }
        public GrainId GrainId { get; set; }
    }
}
