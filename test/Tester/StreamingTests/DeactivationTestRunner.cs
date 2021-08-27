using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Streams;
using Orleans.Internal;
using UnitTests.GrainInterfaces;
using Xunit;
using Orleans.Runtime;

namespace UnitTests.StreamingTests
{
    class DeactivationTestRunner
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
        private readonly string streamProviderName;
        private IClusterClient client;

        private class Counter
        {
            public int Value { get; private set; }

            public Task Increment()
            {
                Value++;
                return Task.CompletedTask;
            }

            public void Clear()
            {
                Value = 0;
            }
        }

        public DeactivationTestRunner(string streamProviderName, IClusterClient client)
        {
            if (string.IsNullOrWhiteSpace(streamProviderName))
            {
                throw new ArgumentNullException(nameof(streamProviderName));
            }

            if (client == null) throw new ArgumentNullException(nameof(client));

            this.streamProviderName = streamProviderName;
            this.client = client;
        }

        public async Task DeactivationTest(Guid streamGuid, string streamNamespace)
        {
            // get producer and consumer
            var producer = this.client.GetGrain<ISampleStreaming_ProducerGrain>(Guid.NewGuid());
            var consumer = this.client.GetGrain<IMultipleSubscriptionConsumerGrain>(Guid.NewGuid());

            // subscribe (PubSubRendezvousGrain will have one consumer)
            StreamSubscriptionHandle<int> subscriptionHandle = await consumer.BecomeConsumer(streamGuid, streamNamespace, streamProviderName);

            // produce one message (PubSubRendezvousGrain will have one consumer and one producer)
            await producer.BecomeProducer(streamGuid, streamNamespace, streamProviderName);
            await producer.Produce();

            var count = await consumer.GetNumberConsumed();

            var consumerGrainReceivedStreamMessage = count[subscriptionHandle].Item1 == 1;
            Assert.True(consumerGrainReceivedStreamMessage);

            // Deactivate all of the pub sub rendesvous grains
            var rendesvousGrains = await client.GetGrain<IManagementGrain>(0).GetActiveGrains(GrainType.Create("pubsubrendezvous"));
            foreach (var grainId in rendesvousGrains)
            {
                var grain = client.GetGrain<IGrainManagementExtension>(grainId);
                await grain.DeactivateOnIdle();
            }

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
            var producer = this.client.GetGrain<ISampleStreaming_ProducerGrain>(Guid.NewGuid());

            var count = new Counter();
            // get stream and subscribe
            IStreamProvider streamProvider = this.client.GetStreamProvider(streamProviderName);
            var stream = streamProvider.GetStream<int>(streamGuid, streamNamespace);
            StreamSubscriptionHandle<int> subscriptionHandle = await stream.SubscribeAsync((e, t) => count.Increment());

            // produce one message (PubSubRendezvousGrain will have one consumer and one producer)
            await producer.BecomeProducer(streamGuid, streamNamespace, streamProviderName);
            await producer.Produce();

            Assert.Equal(1, count.Value);

            // Deactivate all of the pub sub rendesvous grains
            var rendesvousGrains = await client.GetGrain<IManagementGrain>(0).GetActiveGrains(GrainType.Create("pubsubrendezvous"));
            foreach (var grainId in rendesvousGrains)
            {
                var grain = client.GetGrain<IGrainManagementExtension>(grainId);
                await grain.DeactivateOnIdle();
            }

            // deactivating PubSubRendezvousGrain and SampleStreaming_ProducerGrain during the same GC cycle causes a deadlock
            // resume producing after the PubSubRendezvousGrain and the SampleStreaming_ProducerGrain grains have been deactivated:
            await producer.BecomeProducer(streamGuid, streamNamespace, streamProviderName).WithTimeout(Timeout, "BecomeProducer is hung due to deactivation deadlock");
            await producer.Produce().WithTimeout(Timeout, "Produce is hung due to deactivation deadlock");

            Assert.Equal(2, count.Value); // Client consumer grain did not receive stream messages after PubSubRendezvousGrain and SampleStreaming_ProducerGrain reactivation
        }
    }
}
