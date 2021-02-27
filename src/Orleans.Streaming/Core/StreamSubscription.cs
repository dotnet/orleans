using System;
using Orleans.Runtime;

namespace Orleans.Streams.Core
{
    [Serializable]
    [Immutable]
    [GenerateSerializer]
    public class StreamSubscription
    {
        public StreamSubscription(Guid subscriptionId, string streamProviderName, StreamId streamId, GrainId grainId)
        {
            this.SubscriptionId = subscriptionId;
            this.StreamProviderName = streamProviderName;
            this.StreamId = streamId;
            this.GrainId = grainId;
        }

        [Id(1)]
        public Guid SubscriptionId { get; set; }
        [Id(2)]
        public string StreamProviderName { get; set; }
        [Id(3)]
        public StreamId StreamId { get; set; }
        [Id(4)]
        public GrainId GrainId { get; set; }
    }
}
