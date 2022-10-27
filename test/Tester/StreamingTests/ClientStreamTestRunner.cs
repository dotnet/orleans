
using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using UnitTests.GrainInterfaces;
using Xunit;
using Xunit.Abstractions;

namespace Tester.StreamingTests
{
    public class ClientStreamTestRunner
    {
        private static readonly Func<Task<int>> DefaultDeliveryFailureCount = () => Task.FromResult(0); 
        private static readonly TimeSpan _timeout = TimeSpan.FromMinutes(3);

        private readonly TestCluster testHost;
        public ClientStreamTestRunner(TestCluster testHost)
        {
            this.testHost = testHost;
        }

        public async Task StreamProducerOnDroppedClientTest(string streamProviderName, string streamNamespace)
        {
            const int eventsProduced = 10;
            Guid streamGuid = Guid.NewGuid();

            await ProduceEventsFromClient(streamProviderName, streamGuid, streamNamespace, eventsProduced);

            // Hard kill client
            await testHost.KillClientAsync();
            
            // make sure dead client has had time to drop
            await Task.Delay(Constants.DEFAULT_CLIENT_DROP_TIMEOUT + TimeSpan.FromSeconds(5));

            // initialize new client
            await testHost.InitializeClientAsync();

            // run test again.
            await ProduceEventsFromClient(streamProviderName, streamGuid, streamNamespace, eventsProduced);
        }

        public async Task StreamConsumerOnDroppedClientTest(string streamProviderName, string streamNamespace, ITestOutputHelper output, Func<Task<int>> getDeliveryFailureCount = null, bool waitForRetryTimeouts = false)
        {
            getDeliveryFailureCount = getDeliveryFailureCount ?? DefaultDeliveryFailureCount;

            Guid streamGuid = Guid.NewGuid();
            int[] eventCount = {0};

            // become stream consumers
            await SubscribeToStream(streamProviderName, streamGuid, streamNamespace,
                (e, t) => { eventCount[0]++; return Task.CompletedTask; });

            // setup producer
            var producer = this.testHost.GrainFactory.GetGrain<ISampleStreaming_ProducerGrain>(Guid.NewGuid());
            await producer.BecomeProducer(streamGuid, streamNamespace, streamProviderName);

            // produce some events
            await producer.StartPeriodicProducing();
            await Task.Delay(TimeSpan.FromMilliseconds(1000));
            await producer.StopPeriodicProducing();

            // check counts
            await TestingUtils.WaitUntilAsync(lastTry => CheckCounters(() => Task.FromResult(eventCount[0]), producer.GetNumberProduced, lastTry), _timeout);

            // Hard kill client
            await testHost.KillClientAsync();

            // make sure dead client has had time to drop
            await Task.Delay(Constants.DEFAULT_CLIENT_DROP_TIMEOUT + TimeSpan.FromSeconds(5));

            // initialize new client
            await testHost.InitializeClientAsync();

            eventCount[0] = 0;

            // become stream consumers
            await SubscribeToStream(streamProviderName, streamGuid, streamNamespace,
                (e, t) => { eventCount[0]++; return Task.CompletedTask; });

            // setup producer
            producer = this.testHost.GrainFactory.GetGrain<ISampleStreaming_ProducerGrain>(Guid.NewGuid());
            await producer.BecomeProducer(streamGuid, streamNamespace, streamProviderName);

            // produce more events
            await producer.StartPeriodicProducing();
            await Task.Delay(TimeSpan.FromMilliseconds(1000));
            await producer.StopPeriodicProducing();

            // give strem retry policy time to fail
            if (waitForRetryTimeouts)
            {
                await Task.Delay(TimeSpan.FromSeconds(90));
            }

            // check counts
            await TestingUtils.WaitUntilAsync(lastTry => CheckCounters(() => Task.FromResult(eventCount[0]), producer.GetNumberProduced, lastTry), _timeout);
            int deliveryFailureCount = await getDeliveryFailureCount();
            Assert.Equal(0, deliveryFailureCount);
        }

        private Task SubscribeToStream(string streamProviderName, Guid streamGuid, string streamNamespace,
            Func<int, StreamSequenceToken, Task> onNextAsync)
        {
            IStreamProvider streamProvider = this.testHost.Client.GetStreamProvider(streamProviderName);
            IAsyncObservable<int> stream = streamProvider.GetStream<int>(streamNamespace, streamGuid);
            return stream.SubscribeAsync(onNextAsync);
        }

        private async Task ProduceEventsFromClient(string streamProviderName, Guid streamGuid, string streamNamespace, int eventsProduced)
        {
            // get reference to a consumer
            var consumer = this.testHost.GrainFactory.GetGrain<ISampleStreaming_ConsumerGrain>(Guid.NewGuid());

            // subscribe
            await consumer.BecomeConsumer(streamGuid, streamNamespace, streamProviderName);

            // generate events
            await GenerateEvents(streamProviderName, streamGuid, streamNamespace, eventsProduced);

            // make sure all went well
            await TestingUtils.WaitUntilAsync(lastTry => CheckCounters(() => consumer.GetNumberConsumed(), () => Task.FromResult(eventsProduced), lastTry), _timeout);
        }

        private async Task GenerateEvents(string streamProviderName, Guid streamGuid, string streamNamespace, int produceCount)
        {
            IStreamProvider streamProvider = this.testHost.Client.GetStreamProvider(streamProviderName);
            IAsyncObserver<int> observer = streamProvider.GetStream<int>(streamNamespace, streamGuid);
            for (int i = 0; i < produceCount; i++)
            {
                await observer.OnNextAsync(i);
            }
        }

        private async Task<bool> CheckCounters(Func<Task<int>> getConsumed, Func<Task<int>> getProduced, bool assertIsTrue)
        {
            int eventsProduced = await getProduced();
            int numConsumed = await getConsumed();
            if (!assertIsTrue) return eventsProduced == numConsumed;
            Assert.Equal(eventsProduced, numConsumed);
            return true;
        }
    }
}
