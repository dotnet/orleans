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

        public GuidId CreateSubscriptionId(StreamId streamId, IStreamConsumerExtension streamConsumer)
        {
            Guid subscriptionId = SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid());
            return GuidId.GetGuidId(subscriptionId);
        }

        public async Task<bool> FaultSubscription(StreamId streamId, GuidId subscriptionId)
        {
            var streamRendezvous = GetRendezvousGrain(streamId);
            await streamRendezvous.FaultSubscription(subscriptionId);
            return true;
        }
    }
}
