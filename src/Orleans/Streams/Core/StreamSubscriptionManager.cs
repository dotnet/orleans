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
        private readonly string name;
        private readonly IStreamPubSub streamPubSub;
        public StreamSubscriptionManager(IStreamPubSub streamPubSub, string name)
        {
            this.streamPubSub = streamPubSub;
            this.name = name;
        }

        public async Task<StreamSubscription> AddSubscription(IStreamIdentity streamIdentity, GrainReference grainRef)
        {
            var consumer = grainRef.AsReference<IStreamConsumerExtension>();
            var streamProviderName = this.name;
            var streamId = StreamId.GetStreamId(streamIdentity.Guid, streamProviderName, streamIdentity.Namespace);
            var subscriptionId = streamPubSub.CreateSubscriptionId(
                streamId, consumer);
            await streamPubSub.RegisterConsumer(subscriptionId, streamId, streamProviderName, consumer, null);
            var newSub = new StreamSubscription(subscriptionId.Guid, streamProviderName, streamId, grainRef.GrainId);
            return newSub;
        }

        public async Task RemoveSubscription(IStreamIdentity streamId, Guid subscriptionId)
        {
            var streamProviderName = this.name;
            await streamPubSub.UnregisterConsumer(GuidId.GetGuidId(subscriptionId), (StreamId)streamId, streamProviderName, false);
        }

        public Task<IEnumerable<StreamSubscription>> GetSubscriptions(IStreamIdentity streamIdentity)
        {
            var streamProviderName = this.name;
            var streamId = StreamId.GetStreamId(streamIdentity.Guid, streamProviderName, streamIdentity.Namespace);
            return streamPubSub.GetAllSubscriptions(streamId).ContinueWith(subs => subs.Result.AsEnumerable());
        }
    }

}

