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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;
using TestGrainInterfaces;
using UnitTests.SampleStreaming;
using UnitTests.Tester;


namespace Tester.StreamingTests
{
    class PubSubDeactivationTestRunner
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
        private readonly string streamProviderName;
        private readonly Logger logger;

        private class Counter
        {
            public int Value { get; private set; }

            public Task Increment()
            {
                Value++;
                return TaskDone.Done;
            }

            public void Clear()
            {
                Value = 0;
            }
        }

        public PubSubDeactivationTestRunner(string streamProviderName, Logger logger)
        {
            if (string.IsNullOrWhiteSpace(streamProviderName))
            {
                throw new ArgumentNullException("streamProviderName");
            }
            this.streamProviderName = streamProviderName;
            this.logger = logger;
        }

        public async Task DeactivationTest(Guid streamGuid, string streamNamespace)
        {
            // get producer and consumer
            ISampleStreaming_ProducerGrain producer = SampleStreaming_ProducerGrainFactory.GetGrain(Guid.NewGuid());
            IMultipleSubscriptionConsumerGrain consumer = MultipleSubscriptionConsumerGrainFactory.GetGrain(Guid.NewGuid());

            Thread.Sleep(5000);

            // subscribe (PubSubRendezvousGrain will have one consumer)
            StreamSubscriptionHandle<int> subscriptionHandle = await consumer.BecomeConsumer(streamGuid, streamNamespace, streamProviderName);

            // produce one message (PubSubRendezvousGrain will have one consumer and one producer)
            await producer.BecomeProducer(streamGuid, streamNamespace, streamProviderName);
            await producer.Produce();

            var count = await consumer.GetNumberConsumed();
            Assert.AreEqual(count[subscriptionHandle], 1, "Consumer grain has not received stream message");

            Thread.Sleep(180000); // wait for the PubSubRendezvousGrain and the SampleStreaming_ProducerGrain to be deactivated
            // deactivating PubSubRendezvousGrain and SampleStreaming_ProducerGrain during the same GC cycle causes a deadlock

            //confirm that the silo continues to operate despite the deactivation deadlock:

            // consumer grain AgeLimit is set to 2hr in configuration - should still be active and accessible:
            count = await consumer.GetNumberConsumed();
            Assert.AreEqual(count[subscriptionHandle], 1, "Consumer grain has been deactivated");

            // resume producing after the PubSubRendezvousGrain and the SampleStreaming_ProducerGrain grains have been deactivated:
            await producer.BecomeProducer(streamGuid, streamNamespace, streamProviderName);
            await producer.Produce();

            // consumer grain should continue to receive stream messages despite deactivation hang:
            count = await consumer.GetNumberConsumed();
            Assert.AreEqual(count[subscriptionHandle], 2, "Consumer did not receive stream messages after PubSubRendezvousGrain and SampleStreaming_ProducerGrain reactivation");
        }
        public async Task DeactivationTestWithClientGrain(Guid streamGuid, string streamNamespace)
        {
            // get producer and consumer
            ISampleStreaming_ProducerGrain producer = SampleStreaming_ProducerGrainFactory.GetGrain(Guid.NewGuid());

            var count = new Counter();
            // get stream
            IStreamProvider streamProvider = Orleans.GrainClient.GetStreamProvider(streamProviderName);
            var stream = streamProvider.GetStream<int>(streamGuid, streamNamespace);
            // subscribe
            StreamSubscriptionHandle<int> handle = await stream.SubscribeAsync((e, t) => count.Increment());

            Thread.Sleep(5000);

            //// subscribe (PubSubRendezvousGrain will have one consumer)
            //StreamSubscriptionHandle<int> subscriptionHandle = await consumer.BecomeConsumer(streamGuid, streamNamespace, streamProviderName);
            // produce one message (PubSubRendezvousGrain will have one consumer and one producer)
            await producer.BecomeProducer(streamGuid, streamNamespace, streamProviderName);
            await producer.Produce();

            Assert.AreEqual(count.Value, 1, "Client consumer grain has not received stream message");

            Thread.Sleep(180000); // wait for the PubSubRendezvousGrain and the SampleStreaming_ProducerGrain to be deactivated
            // deactivating PubSubRendezvousGrain and SampleStreaming_ProducerGrain during the same GC cycle causes a deadlock

            //confirm that the silo continues to operate despite the deactivation deadlock:

            // resume producing after the PubSubRendezvousGrain and the SampleStreaming_ProducerGrain grains have been deactivated:
            await producer.BecomeProducer(streamGuid, streamNamespace, streamProviderName);
            await producer.Produce();

            Assert.AreEqual(count.Value, 2, "Client consumer grain did not receive stream messages after PubSubRendezvousGrain and SampleStreaming_ProducerGrain reactivation");
        }

    }
}
