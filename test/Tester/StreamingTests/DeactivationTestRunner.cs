using System;
using System.Threading.Tasks;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;

namespace UnitTests.StreamingTests
{
    class DeactivationTestRunner
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

        public DeactivationTestRunner(string streamProviderName, Logger logger)
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
            var producer = GrainClient.GrainFactory.GetGrain<ISampleStreaming_ProducerGrain>(Guid.NewGuid());
            var consumer = GrainClient.GrainFactory.GetGrain<IMultipleSubscriptionConsumerGrain>(Guid.NewGuid());

            // subscribe (PubSubRendezvousGrain will have one consumer)
            StreamSubscriptionHandle<int> subscriptionHandle = await consumer.BecomeConsumer(streamGuid, streamNamespace, streamProviderName);

            // produce one message (PubSubRendezvousGrain will have one consumer and one producer)
            await producer.BecomeProducer(streamGuid, streamNamespace, streamProviderName);
            await producer.Produce();

            var count = await consumer.GetNumberConsumed();
            Assert.AreEqual(count[subscriptionHandle].Item1, 1, "Consumer grain has not received stream message");

            //TODO: trigger deactivation programmatically
            await Task.Delay(TimeSpan.FromMilliseconds(130000)); // wait for the PubSubRendezvousGrain and the SampleStreaming_ProducerGrain to be deactivated

            // deactivating PubSubRendezvousGrain and SampleStreaming_ProducerGrain during the same GC cycle causes a deadlock
            // resume producing after the PubSubRendezvousGrain and the SampleStreaming_ProducerGrain grains have been deactivated:

            await producer.BecomeProducer(streamGuid, streamNamespace, streamProviderName).WithTimeout(Timeout, "BecomeProducer is hung due to deactivation deadlock");
            await producer.Produce().WithTimeout(Timeout, "Produce is hung due to deactivation deadlock");

            // consumer grain should continue to receive stream messages:
            count = await consumer.GetNumberConsumed();
            Assert.AreEqual(count[subscriptionHandle].Item1, 2, "Consumer did not receive stream messages after PubSubRendezvousGrain and SampleStreaming_ProducerGrain reactivation");
        }
        public async Task DeactivationTest_ClientConsumer(Guid streamGuid, string streamNamespace)
        {
            // get producer and consumer
            var producer = GrainClient.GrainFactory.GetGrain<ISampleStreaming_ProducerGrain>(Guid.NewGuid());

            var count = new Counter();
            // get stream and subscribe
            IStreamProvider streamProvider = GrainClient.GetStreamProvider(streamProviderName);
            var stream = streamProvider.GetStream<int>(streamGuid, streamNamespace);
            StreamSubscriptionHandle<int> subscriptionHandle = await stream.SubscribeAsync((e, t) => count.Increment());

            // produce one message (PubSubRendezvousGrain will have one consumer and one producer)
            await producer.BecomeProducer(streamGuid, streamNamespace, streamProviderName);
            await producer.Produce();

            Assert.AreEqual(count.Value, 1, "Client consumer grain has not received stream message");

            //TODO: trigger deactivation programmatically
            await Task.Delay(TimeSpan.FromMilliseconds(130000)); // wait for the PubSubRendezvousGrain and the SampleStreaming_ProducerGrain to be deactivated

            // deactivating PubSubRendezvousGrain and SampleStreaming_ProducerGrain during the same GC cycle causes a deadlock
            // resume producing after the PubSubRendezvousGrain and the SampleStreaming_ProducerGrain grains have been deactivated:
            await producer.BecomeProducer(streamGuid, streamNamespace, streamProviderName).WithTimeout(Timeout, "BecomeProducer is hung due to deactivation deadlock");
            await producer.Produce().WithTimeout(Timeout, "Produce is hung due to deactivation deadlock");

            Assert.AreEqual(count.Value, 2, "Client consumer grain did not receive stream messages after PubSubRendezvousGrain and SampleStreaming_ProducerGrain reactivation");
        }

    }
}
