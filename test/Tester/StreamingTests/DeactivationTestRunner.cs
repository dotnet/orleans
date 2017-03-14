using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.TestingHost.Utils;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.StreamingTests
{
    class DeactivationTestRunner
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
        private readonly string streamProviderName;
        private readonly Logger logger;
        private readonly IGrainFactory grainFactory;

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

        public DeactivationTestRunner(string streamProviderName, Logger logger, IGrainFactory grainFactory)
        {
            if (string.IsNullOrWhiteSpace(streamProviderName))
            {
                throw new ArgumentNullException("streamProviderName");
            }
            this.streamProviderName = streamProviderName;
            this.logger = logger;
            this.grainFactory = grainFactory;
        }

        public async Task DeactivationTest(Guid streamGuid, string streamNamespace)
        {
            // get producer and consumer
            var producer = this.grainFactory.GetGrain<ISampleStreaming_ProducerGrain>(Guid.NewGuid());
            var consumer = this.grainFactory.GetGrain<IMultipleSubscriptionConsumerGrain>(Guid.NewGuid());

            // subscribe (PubSubRendezvousGrain will have one consumer)
            StreamSubscriptionHandle<int> subscriptionHandle = await consumer.BecomeConsumer(streamGuid, streamNamespace, streamProviderName);

            // produce one message (PubSubRendezvousGrain will have one consumer and one producer)
            await producer.BecomeProducer(streamGuid, streamNamespace, streamProviderName);
            await producer.Produce();

            var count = await consumer.GetNumberConsumed();

            var consumerGrainReceivedStreamMessage = count[subscriptionHandle].Item1 == 1;
            Assert.True(consumerGrainReceivedStreamMessage);

            //TODO: trigger deactivation programmatically
            await Task.Delay(TimeSpan.FromMilliseconds(130000)); // wait for the PubSubRendezvousGrain and the SampleStreaming_ProducerGrain to be deactivated

            // deactivating PubSubRendezvousGrain and SampleStreaming_ProducerGrain during the same GC cycle causes a deadlock
            // resume producing after the PubSubRendezvousGrain and the SampleStreaming_ProducerGrain grains have been deactivated:

            await producer.BecomeProducer(streamGuid, streamNamespace, streamProviderName).WithTimeout(Timeout, "BecomeProducer is hung due to deactivation deadlock");
            await producer.Produce().WithTimeout(Timeout, "Produce is hung due to deactivation deadlock");

            // consumer grain should continue to receive stream messages:
            count = await consumer.GetNumberConsumed();

            Assert.True(count[subscriptionHandle].Item1 == 2, "Consumer did not receive stream messages after PubSubRendezvousGrain and SampleStreaming_ProducerGrain reactivation");
        }
        public async Task DeactivationTest_ClientConsumer(Guid streamGuid, string streamNamespace)
        {
            // get producer and consumer
            var producer = this.grainFactory.GetGrain<ISampleStreaming_ProducerGrain>(Guid.NewGuid());

            var count = new Counter();
            // get stream and subscribe
            IStreamProvider streamProvider = GrainClient.GetStreamProvider(streamProviderName);
            var stream = streamProvider.GetStream<int>(streamGuid, streamNamespace);
            StreamSubscriptionHandle<int> subscriptionHandle = await stream.SubscribeAsync((e, t) => count.Increment());

            // produce one message (PubSubRendezvousGrain will have one consumer and one producer)
            await producer.BecomeProducer(streamGuid, streamNamespace, streamProviderName);
            await producer.Produce();

            Assert.Equal(1, count.Value);

            //TODO: trigger deactivation programmatically
            await Task.Delay(TimeSpan.FromMilliseconds(130000)); // wait for the PubSubRendezvousGrain and the SampleStreaming_ProducerGrain to be deactivated

            // deactivating PubSubRendezvousGrain and SampleStreaming_ProducerGrain during the same GC cycle causes a deadlock
            // resume producing after the PubSubRendezvousGrain and the SampleStreaming_ProducerGrain grains have been deactivated:
            await producer.BecomeProducer(streamGuid, streamNamespace, streamProviderName).WithTimeout(Timeout, "BecomeProducer is hung due to deactivation deadlock");
            await producer.Produce().WithTimeout(Timeout, "Produce is hung due to deactivation deadlock");

            Assert.Equal(2, count.Value); // Client consumer grain did not receive stream messages after PubSubRendezvousGrain and SampleStreaming_ProducerGrain reactivation
        }

    }
}
