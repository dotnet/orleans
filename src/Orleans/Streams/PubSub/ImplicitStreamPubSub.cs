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
    internal class ImplicitStreamPubSub : IStreamPubSub
    {
        private readonly ImplicitStreamSubscriberTable implicitTable;

        public ImplicitStreamPubSub(ImplicitStreamSubscriberTable implicitPubSubTable)
        {
            if (implicitPubSubTable == null)
            {
                throw new ArgumentNullException("implicitPubSubTable");
            }

            this.implicitTable = implicitPubSubTable;
        }

        public Task<ISet<PubSubSubscriptionState>> RegisterProducer(StreamId streamId, string streamProvider, IStreamProducerExtension streamProducer)
        {
            ISet<PubSubSubscriptionState> result = new HashSet<PubSubSubscriptionState>();
            if (String.IsNullOrWhiteSpace(streamId.Namespace)) return Task.FromResult(result);

            IDictionary<Guid, IStreamConsumerExtension> implicitSubscriptions = implicitTable.GetImplicitSubscribers(streamId);
            foreach (var kvp in implicitSubscriptions)
            {
                GuidId subscriptionId = GuidId.GetGuidId(kvp.Key);
                result.Add(new PubSubSubscriptionState(subscriptionId, streamId, kvp.Value));
            }
            return Task.FromResult(result);
        }

        public Task UnregisterProducer(StreamId streamId, string streamProvider, IStreamProducerExtension streamProducer)
        {
            return TaskDone.Done;
        }

        public Task RegisterConsumer(GuidId subscriptionId, StreamId streamId, string streamProvider, IStreamConsumerExtension streamConsumer, IStreamFilterPredicateWrapper filter)
        {
            if (!IsImplicitSubscriber(streamConsumer, streamId))
            {
                throw new ArgumentOutOfRangeException(streamId.ToString(), "Only implicit subscriptions are supported.");
            }
            return TaskDone.Done;
        }

        public Task UnregisterConsumer(GuidId subscriptionId, StreamId streamId, string streamProvider)
        {
            return TaskDone.Done;
        }

        public Task<int> ProducerCount(Guid streamId, string streamProvider, string streamNamespace)
        {
            return Task.FromResult(0);
        }

        public Task<int> ConsumerCount(Guid streamId, string streamProvider, string streamNamespace)
        {
            return Task.FromResult(0);
        }

        public Task<List<GuidId>> GetAllSubscriptions(StreamId streamId, IStreamConsumerExtension streamConsumer)
        {
            if (!IsImplicitSubscriber(streamConsumer, streamId))
            {
                throw new ArgumentOutOfRangeException(streamId.ToString(), "Only implicit subscriptions are supported.");
            }
            return Task.FromResult(new List<GuidId> { GuidId.GetGuidId(streamConsumer.GetPrimaryKey()) });
        }

        internal bool IsImplicitSubscriber(IAddressable addressable, StreamId streamId)
        {
            return implicitTable.IsImplicitSubscriber(GrainExtensions.GetGrainId(addressable), streamId);
        }

        internal bool IsImplicitSubscriber(GuidId subscriptionId, StreamId streamId)
        {
            return SubscriptionMarker.IsImplicitSubscription(subscriptionId.Guid);
        }

        public GuidId CreateSubscriptionId(StreamId streamId, IStreamConsumerExtension streamConsumer)
        {
            GrainId grainId = GrainExtensions.GetGrainId(streamConsumer);
            Guid subscriptionGuid;
            if (!implicitTable.TryGetImplicitSubscriptionGuid(grainId, streamId, out subscriptionGuid))
            {
                throw new ArgumentOutOfRangeException(streamId.ToString(), "Only implicit subscriptions are supported.");
            }
            return GuidId.GetGuidId(subscriptionGuid);
        }

        public Task<bool> FaultSubscription(StreamId streamId, GuidId subscriptionId)
        {
            return Task.FromResult(false);
        }
    }
}
