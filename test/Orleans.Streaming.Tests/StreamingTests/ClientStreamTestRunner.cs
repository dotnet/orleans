using Orleans.Runtime;
using Orleans.Streams;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using TestExtensions;
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

            await ProduceEventsToClient(streamProviderName, streamGuid, streamNamespace, 10, eventCount);

            // Hard kill client
            await testHost.KillClientAsync();

            // Not all providers emit subscription-removal diagnostics for client disconnect cleanup.
            await Task.Delay(Constants.DEFAULT_CLIENT_DROP_TIMEOUT + TimeSpan.FromSeconds(5));

            // initialize new client
            await testHost.InitializeClientAsync();

            eventCount[0] = 0;

            await ProduceEventsToClient(streamProviderName, streamGuid, streamNamespace, 10, eventCount);

            // give strem retry policy time to fail
            if (waitForRetryTimeouts)
            {
                await Task.Delay(TimeSpan.FromSeconds(90));
            }

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
            using var observer = StreamingDiagnosticObserver.Create();
            using var cts = new CancellationTokenSource(_timeout);
            var streamId = StreamId.Create(streamNamespace, streamGuid);

            // get reference to a consumer
            var consumer = this.testHost.GrainFactory.GetGrain<ISampleStreaming_ConsumerGrain>(Guid.NewGuid());

            // subscribe
            await consumer.BecomeConsumer(streamGuid, streamNamespace, streamProviderName);
            var subscription = await observer.WaitForSubscriptionRegisteredAsync(streamId, streamProviderName, cts.Token);
            try
            {
                // generate events
                await GenerateEvents(streamProviderName, streamGuid, streamNamespace, eventsProduced);

                await observer.WaitForItemDeliveryCountAsync(streamId, subscription.SubscriptionId, eventsProduced, streamProviderName, cts.Token);
                Assert.Equal(eventsProduced, await consumer.GetNumberConsumed());
            }
            finally
            {
                await consumer.StopConsuming();
            }
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

        private async Task ProduceEventsToClient(string streamProviderName, Guid streamGuid, string streamNamespace, int eventsProduced, int[] eventCount)
        {
            using var observer = StreamingDiagnosticObserver.Create();
            using var cts = new CancellationTokenSource(_timeout);
            var streamId = StreamId.Create(streamNamespace, streamGuid);

            await SubscribeToStream(streamProviderName, streamGuid, streamNamespace,
                (e, t) =>
                {
                    eventCount[0]++;
                    return Task.CompletedTask;
                });

            var producer = this.testHost.GrainFactory.GetGrain<ISampleStreaming_ProducerGrain>(Guid.NewGuid());
            await producer.BecomeProducer(streamGuid, streamNamespace, streamProviderName);

            await ProduceExactCountAsync(producer, eventsProduced);
            await observer.WaitForItemDeliveryCountAsync(streamId, eventsProduced, streamProviderName, cts.Token);

            Assert.Equal(eventsProduced, eventCount[0]);
            Assert.Equal(eventsProduced, await producer.GetNumberProduced());
        }

        private static async Task ProduceExactCountAsync(ISampleStreaming_ProducerGrain producer, int count)
        {
            for (var i = 0; i < count; i++)
            {
                await producer.Produce();
            }
        }
    }
}
