
using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Streams;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using Xunit;

namespace Tester.StreamingTests
{
    public class ClientStreamTestRunner
    {
        private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

        private readonly TestingSiloHost testHost;
        public ClientStreamTestRunner(TestingSiloHost testHost)
        {
            this.testHost = testHost;
        }

        public async Task StreamProducerOnDroppedClientTest(string streamProviderName, string streamNamespace)
        {
            const int eventsProduced = 10;
            Guid streamGuid = Guid.NewGuid();

            await ProduceEventsFromClient(streamProviderName, streamGuid, streamNamespace, eventsProduced);

            // Hard kill client
            testHost.KillClient();

            // make sure dead client has had time to drop
            await Task.Delay(testHost.Globals.ClientDropTimeout + TimeSpan.FromSeconds(5));

            // initialize new client
            testHost.InitializeClient();

            // run test again.
            await ProduceEventsFromClient(streamProviderName, streamGuid, streamNamespace, eventsProduced);
        }

        private async Task ProduceEventsFromClient(string streamProviderName, Guid streamGuid, string streamNamespace, int eventsProduced)
        {
            // get reference to a consumer
            var consumer = GrainClient.GrainFactory.GetGrain<ISampleStreaming_ConsumerGrain>(Guid.NewGuid());

            // subscribe
            await consumer.BecomeConsumer(streamGuid, streamNamespace, streamProviderName);

            // generate events
            await GenerateEvents(streamProviderName, streamGuid, streamNamespace, eventsProduced);

            // make sure all went well
            await TestingUtils.WaitUntilAsync(lastTry => CheckCounters(consumer, eventsProduced, lastTry), _timeout);
        }

        private async Task GenerateEvents(string streamProviderName, Guid streamGuid, string streamNamespace, int produceCount)
        {
            IStreamProvider streamProvider = GrainClient.GetStreamProvider(streamProviderName);
            IAsyncObserver<int> observer = streamProvider.GetStream<int>(streamGuid, streamNamespace);
            for (int i = 0; i < produceCount; i++)
            {
                await observer.OnNextAsync(i);
            }
        }

        private async Task<bool> CheckCounters(ISampleStreaming_ConsumerGrain consumer, int eventsProduced, bool assertIsTrue)
        {
            var numConsumed = await consumer.GetNumberConsumed();
            if (!assertIsTrue) return eventsProduced == numConsumed;
            Assert.Equal(eventsProduced, numConsumed);
            return true;
        }
    }
}
