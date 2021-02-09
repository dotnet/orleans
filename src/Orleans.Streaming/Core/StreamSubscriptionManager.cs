using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Orleans.Streams.Core
{
    internal class StreamSubscriptionManager: IStreamSubscriptionManager
    {
        private readonly string type;
        private readonly IStreamPubSub streamPubSub;

        public StreamSubscriptionManager(IStreamPubSub streamPubSub, string managerType)
        {
            this.streamPubSub = streamPubSub;
            this.type = managerType;
        }

        public async Task<StreamSubscription> AddSubscription(string streamProviderName, StreamId streamId, GrainReference grainRef)
        {
            var consumer = grainRef.AsReference<IStreamConsumerExtension>();
            var internalStreamId = new InternalStreamId(streamProviderName, streamId);
            var subscriptionId = streamPubSub.CreateSubscriptionId(internalStreamId, consumer);
            await streamPubSub.RegisterConsumer(subscriptionId, internalStreamId, consumer, null);
            var newSub = new StreamSubscription(subscriptionId.Guid, streamProviderName, streamId, grainRef.GrainId);
            return newSub;
        }

        public async Task RemoveSubscription(string streamProviderName, StreamId streamId, Guid subscriptionId)
        {
            var internalStreamId = new InternalStreamId(streamProviderName, streamId);
            await streamPubSub.UnregisterConsumer(GuidId.GetGuidId(subscriptionId), internalStreamId);
        }

        public Task<IEnumerable<StreamSubscription>> GetSubscriptions(string streamProviderName, StreamId streamId)
        {
            var internalStreamId = new InternalStreamId(streamProviderName, streamId);
            return streamPubSub.GetAllSubscriptions(internalStreamId).ContinueWith(subs => subs.Result.AsEnumerable());
        }
    }

}

