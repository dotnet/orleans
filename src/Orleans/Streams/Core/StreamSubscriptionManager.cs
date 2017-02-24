using Orleans.Runtime;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Orleans.Streams.Core
{
    internal class StreamSubscriptionManager: IStreamSubscriptionManager
    {
        private readonly IStreamPubSub streamPubSub;
        private readonly string streamProviderName;
        public StreamSubscriptionManager(IStreamPubSub pubsub, string streamProviderName)
        {
            this.streamPubSub = pubsub;
            this.streamProviderName = streamProviderName;
        }
        public async Task<StreamSubscription> AddSubscription(IStreamIdentity streamIdentity, GrainReference grainRef)
        {
            var consumer = grainRef.AsReference<IStreamConsumerExtension>();
            var streamId = StreamId.GetStreamId(streamIdentity.Guid, this.streamProviderName, streamIdentity.Namespace);
            var subscriptionId = streamPubSub.CreateSubscriptionId(
                streamId, consumer);
            await streamPubSub.RegisterConsumer(subscriptionId, streamId, this.streamProviderName, consumer, null);
            var newSub = new StreamSubscription(subscriptionId.Guid, this.streamProviderName, streamId, grainRef.GrainId);
            return newSub;
        }

        public async Task RemoveSubscription(IStreamIdentity streamId, Guid subscriptionId)
        {
            await streamPubSub.UnregisterConsumer(GuidId.GetGuidId(subscriptionId), (StreamId)streamId, this.streamProviderName);
        }

        public Task<IEnumerable<StreamSubscription>> GetSubscriptions(IStreamIdentity streamIdentity)
        {
            var streamId = StreamId.GetStreamId(streamIdentity.Guid, this.streamProviderName, streamIdentity.Namespace);
            return streamPubSub.GetAllSubscriptions(streamId).ContinueWith(subs => subs.Result.AsEnumerable());
        }
    }
}
