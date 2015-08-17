/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Streams
{
    internal class GrainBasedPubSubRuntime : IStreamPubSub
    {
        private readonly IGrainFactory grainFactory;

        public GrainBasedPubSubRuntime(IGrainFactory grainFactory)
        {
            this.grainFactory = grainFactory;
        }

        public Task<ISet<PubSubSubscriptionState>> RegisterProducer(StreamId streamId, string streamProvider, IStreamProducerExtension streamProducer)
        {
            var streamRendezvous = GetRendezvousGrain(streamId);
            return streamRendezvous.RegisterProducer(streamId, streamProducer);
        }

        public Task UnregisterProducer(StreamId streamId, string streamProvider, IStreamProducerExtension streamProducer)
        {
            var streamRendezvous = GetRendezvousGrain(streamId);
            return streamRendezvous.UnregisterProducer(streamId, streamProducer);
        }

        public Task RegisterConsumer(GuidId subscriptionId, StreamId streamId, string streamProvider, IStreamConsumerExtension streamConsumer, IStreamFilterPredicateWrapper filter)
        {
            var streamRendezvous = GetRendezvousGrain(streamId);
            return streamRendezvous.RegisterConsumer(subscriptionId, streamId, streamConsumer, filter);
        }

        public Task UnregisterConsumer(GuidId subscriptionId, StreamId streamId, string streamProvider)
        {
            var streamRendezvous = GetRendezvousGrain(streamId);
            return streamRendezvous.UnregisterConsumer(subscriptionId, streamId);
        }

        public Task<int> ProducerCount(Guid guidId, string streamProvider, string streamNamespace)
        {
            StreamId streamId = StreamId.GetStreamId(guidId, streamProvider, streamNamespace);
            var streamRendezvous = GetRendezvousGrain(streamId);
            return streamRendezvous.ProducerCount(streamId);
        }

        public Task<int> ConsumerCount(Guid guidId, string streamProvider, string streamNamespace)
        {
            StreamId streamId = StreamId.GetStreamId(guidId, streamProvider, streamNamespace);
            var streamRendezvous = GetRendezvousGrain(streamId);
            return streamRendezvous.ConsumerCount(streamId);
        }

        public Task<List<GuidId>> GetAllSubscriptions(StreamId streamId, IStreamConsumerExtension streamConsumer)
        {
            var streamRendezvous = GetRendezvousGrain(streamId);
            return streamRendezvous.GetAllSubscriptions(streamId, streamConsumer);
        }

        private IPubSubRendezvousGrain GetRendezvousGrain(StreamId streamId)
        {
            return grainFactory.GetGrain<IPubSubRendezvousGrain>(
                primaryKey: streamId.Guid,
                keyExtension: streamId.ProviderName + "_" + streamId.Namespace);
        }

        public GuidId CreateSubscriptionId(IAddressable requesterAddress, StreamId streamId)
        {
            return GuidId.GetNewGuidId();
        }

        public async Task<bool> FaultSubscription(GuidId subscriptionId, StreamId streamId)
        {
            var streamRendezvous = GetRendezvousGrain(streamId);
            await streamRendezvous.FaultSubscription(subscriptionId);
            return true;
        }
    }
    
    internal class StreamPubSubImpl : IStreamPubSub
    {
        private readonly IStreamPubSub explicitPubSub;
        private readonly ImplicitStreamSubscriberTable implicitPubSub;

        public StreamPubSubImpl(IStreamPubSub explicitPubSub, ImplicitStreamSubscriberTable implicitPubSub)
        {
            if (explicitPubSub == null)
            {
                throw new ArgumentNullException("explicitPubSub");
            }

            if (implicitPubSub == null)
            {
                throw new ArgumentNullException("implicitPubSub");
            }

            this.explicitPubSub = explicitPubSub;
            this.implicitPubSub = implicitPubSub;
        }

        public async Task<ISet<PubSubSubscriptionState>> RegisterProducer(StreamId streamId, string streamProvider, IStreamProducerExtension streamProducer)
        {
            ISet<PubSubSubscriptionState> result = await explicitPubSub.RegisterProducer(streamId, streamProvider, streamProducer);
            if (String.IsNullOrWhiteSpace(streamId.Namespace)) return result;

            IDictionary<Guid,IStreamConsumerExtension> implicitSubscriptions = implicitPubSub.GetImplicitSubscribers(streamId);
            foreach (var kvp in implicitSubscriptions)
            {
                GuidId subscriptionId = GuidId.GetGuidId(kvp.Key);
                // we ignore duplicate entries-- there's no way a programmer could prevent the duplicate entry from being added if we threw an exception to communicate the problem. 
                result.Add(new PubSubSubscriptionState(subscriptionId, streamId, kvp.Value, null));
            }
            return result;
        }

        public Task UnregisterProducer(StreamId streamId, string streamProvider, IStreamProducerExtension streamProducer)
        {
            return IsImplicitSubscriber(streamProducer, streamId) ? TaskDone.Done : 
                explicitPubSub.UnregisterProducer(streamId, streamProvider, streamProducer);
        }

        public Task RegisterConsumer(GuidId subscriptionId, StreamId streamId, string streamProvider, IStreamConsumerExtension streamConsumer, IStreamFilterPredicateWrapper filter)
        {
            return IsImplicitSubscriber(streamConsumer, streamId)
                ? TaskDone.Done
                : explicitPubSub.RegisterConsumer(subscriptionId, streamId, streamProvider, streamConsumer, filter);
        }

        public Task UnregisterConsumer(GuidId subscriptionId, StreamId streamId, string streamProvider)
        {
            return IsImplicitSubscriber(subscriptionId, streamId)
                ? TaskDone.Done
                : explicitPubSub.UnregisterConsumer(subscriptionId, streamId, streamProvider);
        }

        public Task<int> ProducerCount(Guid streamId, string streamProvider, string streamNamespace)
        {
            return explicitPubSub.ProducerCount(streamId, streamProvider, streamNamespace); 
        }

        public Task<int> ConsumerCount(Guid streamId, string streamProvider, string streamNamespace)
        {
            return explicitPubSub.ConsumerCount(streamId, streamProvider, streamNamespace); 
        }

        public async Task<List<GuidId>> GetAllSubscriptions(StreamId streamId, IStreamConsumerExtension streamConsumer)
        {
            return IsImplicitSubscriber(streamConsumer, streamId)
                ? new List<GuidId>( new [] { GuidId.GetGuidId(streamConsumer.GetPrimaryKey()) } )
                : await explicitPubSub.GetAllSubscriptions(streamId, streamConsumer);
        }

        private bool IsImplicitSubscriber(IAddressable addressable, StreamId streamId)
        {
            return implicitPubSub.IsImplicitSubscriber(GrainExtensions.GetGrainId(addressable), streamId);
        }
        private bool IsImplicitSubscriber(GuidId subscriptionId, StreamId streamId)
        {
            return SubscriptionMarker.IsImplicitSubscription(subscriptionId.Guid);
        }

        public GuidId CreateSubscriptionId(IAddressable requesterAddress, StreamId streamId)
        {
            GrainId grainId = GrainExtensions.GetGrainId(requesterAddress);
            Guid subscriptionId;
            if (!implicitPubSub.TryGetImplicitSubscriptionGuid(grainId, streamId, out subscriptionId))
            {
                subscriptionId = SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid());
            }
            return GuidId.GetGuidId(subscriptionId);
        }

        public Task<bool> FaultSubscription(GuidId subscriptionId, StreamId streamId)
        {
            return IsImplicitSubscriber(subscriptionId, streamId)
                ? Task.FromResult(false)
                : explicitPubSub.FaultSubscription(subscriptionId, streamId);
        }
    }
}
