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
        private readonly string type;
        private readonly IStreamPubSub streamPubSub;
        public StreamSubscriptionManager(IStreamPubSub streamPubSub, string managerType)
        {
            this.streamPubSub = streamPubSub;
            this.type = managerType;
        }

        public async Task<StreamSubscription> AddSubscription(string streamProviderName, IStreamIdentity streamIdentity, GrainReference grainRef)
        {
            var consumer = grainRef.AsReference<IStreamConsumerExtension>();
            var streamId = new InternalStreamId(streamProviderName, StreamId.Create(streamIdentity));
            var subscriptionId = streamPubSub.CreateSubscriptionId(
                streamId, consumer);
            await streamPubSub.RegisterConsumer(subscriptionId, streamId, consumer, null);
            var newSub = new StreamSubscription(subscriptionId.Guid, streamProviderName, streamId.StreamId, grainRef.GrainId);
            return newSub;
        }

        public async Task RemoveSubscription(string streamProviderName, IStreamIdentity streamIdentity, Guid subscriptionId)
        {
            var streamId = new InternalStreamId(streamProviderName, StreamId.Create(streamIdentity));
            await streamPubSub.UnregisterConsumer(GuidId.GetGuidId(subscriptionId), streamId);
        }

        public Task<IEnumerable<StreamSubscription>> GetSubscriptions(string streamProviderName, IStreamIdentity streamIdentity)
        {
            var streamId = new InternalStreamId(streamProviderName, StreamId.Create(streamIdentity));
            return streamPubSub.GetAllSubscriptions(streamId).ContinueWith(subs => subs.Result.AsEnumerable());
        }
    }

}

