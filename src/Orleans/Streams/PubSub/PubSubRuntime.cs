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

﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Orleans.Runtime;

namespace Orleans.Streams
{
    internal class GrainBasedPubSubRuntime : IStreamPubSub
    {
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

        public Task RegisterConsumer(GuidId subscriptionId, StreamId streamId, string streamProvider, IStreamConsumerExtension streamConsumer, StreamSequenceToken token, IStreamFilterPredicateWrapper filter)
        {
            var streamRendezvous = GetRendezvousGrain(streamId);
            return streamRendezvous.RegisterConsumer(subscriptionId, streamId, streamConsumer, token, filter);
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

        private static IPubSubRendezvousGrain GetRendezvousGrain(StreamId streamId)
        {
            return (IPubSubRendezvousGrain)GrainClient.InvokeStaticMethodThroughReflection(
                "Orleans",
                "Orleans.Streams.PubSubRendezvousGrainFactory",
                "GetGrain",
                new Type[] { typeof(Guid), typeof(string) },
                new object[] { streamId.Guid, streamId.ProviderName + "_" + streamId.Namespace });
        }

        public GuidId CreateSubscriptionId(IAddressable requesterAddress, StreamId streamId)
        {
            return GuidId.GetNewGuidId();
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

            ISet<IStreamConsumerExtension> implicitSet = implicitPubSub.GetImplicitSubscribers(streamId);
            foreach (var consumer in implicitSet)
            {
                // we ignore duplicate entries-- there's no way a programmer could prevent the duplicate entry from being added if we threw an exception to communicate the problem. 
                GuidId subscriptionId = GuidId.GetGuidId(GrainExtensions.GetGrainId(consumer).GetPrimaryKey());
                result.Add(new PubSubSubscriptionState(subscriptionId, streamId, consumer, null, null));
            }
            return result;
        }

        public Task UnregisterProducer(StreamId streamId, string streamProvider, IStreamProducerExtension streamProducer)
        {
            return IsImplicitSubscriber(streamProducer, streamId) ? TaskDone.Done : 
                explicitPubSub.UnregisterProducer(streamId, streamProvider, streamProducer);
        }

        public Task RegisterConsumer(GuidId subscriptionId, StreamId streamId, string streamProvider, IStreamConsumerExtension streamConsumer, StreamSequenceToken token, IStreamFilterPredicateWrapper filter)
        {
            return IsImplicitSubscriber(streamConsumer, streamId)
                ? TaskDone.Done
                : explicitPubSub.RegisterConsumer(subscriptionId, streamId, streamProvider, streamConsumer, token, filter);
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
            return implicitPubSub.IsImplicitSubscriber(subscriptionId, streamId);
        }

        public GuidId CreateSubscriptionId(IAddressable requesterAddress, StreamId streamId)
        {
            GrainId grainId = GrainExtensions.GetGrainId(requesterAddress);
            // If there is an implicit subscription setup for the provided grain on this provided stream, subscription should match the stream Id.
            // If there is no implicit subscription setup, generate new random unique subscriptionId.
            // TODO: Replace subscription id with statically generated subscriptionId instead of getting it from the streamId.
            return implicitPubSub.IsImplicitSubscriber(grainId, streamId)
                ? GuidId.GetGuidId(grainId.GetPrimaryKey())
                : GuidId.GetNewGuidId();
        }
    }
}