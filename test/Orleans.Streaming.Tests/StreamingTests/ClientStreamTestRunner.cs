using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

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

        public async Task StreamProducerOnDroppedClientTest(
            string streamProviderName,
            string streamNamespace,
            CancellationToken cancellationToken = default)
        {
            const int eventsProduced = 10;
            Guid streamGuid = Guid.NewGuid();

            await ProduceEventsFromClient(streamProviderName, streamGuid, streamNamespace, eventsProduced, cancellationToken);

            // Hard kill client
            var droppedClients = GetConnectedClients();
            using var gatewayObserver = GatewayDiagnosticObserver.Create(testHost);
            await testHost.KillClientAsync();
            await WaitForDroppedClientsAsync(droppedClients, gatewayObserver, cancellationToken);

            // initialize new client
            await testHost.InitializeClientAsync(cancellationToken);

            // run test again.
            await ProduceEventsFromClient(streamProviderName, streamGuid, streamNamespace, eventsProduced, cancellationToken);
        }

        public async Task StreamConsumerOnDroppedClientTest(
            string streamProviderName,
            string streamNamespace,
            ITestOutputHelper? output,
            Func<Task<int>>? getDeliveryFailureCount = null,
            bool waitForRetryTimeouts = false,
            CancellationToken cancellationToken = default)
        {
            var hasDeliveryFailureCounter = getDeliveryFailureCount is not null;
            getDeliveryFailureCount ??= DefaultDeliveryFailureCount;

            Guid streamGuid = Guid.NewGuid();
            var streamId = StreamId.Create(streamNamespace, streamGuid);
            int[] eventCount = {0};

            var droppedSubscriptionId = await ProduceEventsToClient(streamProviderName, streamGuid, streamNamespace, 10, eventCount, cancellationToken);

            // Hard kill client
            var droppedClients = GetConnectedClients();
            using var gatewayObserver = GatewayDiagnosticObserver.Create(testHost);
            using var streamingObserver = StreamingDiagnosticObserver.Create(testHost);
            await testHost.KillClientAsync();
            await WaitForDroppedClientsAsync(droppedClients, gatewayObserver, cancellationToken);

            // initialize new client
            await testHost.InitializeClientAsync(cancellationToken);

            eventCount[0] = 0;

            await ProduceEventsToClient(streamProviderName, streamGuid, streamNamespace, 10, eventCount, cancellationToken);

            // Wait for the dropped client's subscription to be removed after delivery fails.
            if (waitForRetryTimeouts && hasDeliveryFailureCounter)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(_timeout);
                await streamingObserver.WaitForSubscriptionUnregisteredAsync(streamId, droppedSubscriptionId, streamProviderName, cts.Token);
            }

            int deliveryFailureCount = await getDeliveryFailureCount();
            Assert.Equal(0, deliveryFailureCount);
        }

        private Task<StreamSubscriptionHandle<int>> SubscribeToStream(string streamProviderName, Guid streamGuid, string streamNamespace,
            Func<int, StreamSequenceToken?, Task> onNextAsync)
        {
            IStreamProvider streamProvider = this.testHost.Client!.GetStreamProvider(streamProviderName); // The runner deploys the client.
            IAsyncObservable<int> stream = streamProvider.GetStream<int>(streamNamespace, streamGuid);
            return stream.SubscribeAsync(onNextAsync);
        }

        private async Task ProduceEventsFromClient(
            string streamProviderName,
            Guid streamGuid,
            string streamNamespace,
            int eventsProduced,
            CancellationToken cancellationToken)
        {
            using var observer = StreamingDiagnosticObserver.Create(testHost);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_timeout);
            var streamId = StreamId.Create(streamNamespace, streamGuid);

            // get reference to a consumer
            var consumer = this.testHost.GrainFactory!.GetGrain<ISampleStreaming_ConsumerGrain>(Guid.NewGuid()); // The runner deploys the client.

            // subscribe
            await consumer.BecomeConsumer(streamGuid, streamNamespace, streamProviderName, cts.Token);
            var subscription = await observer.WaitForSubscriptionRegisteredAsync(streamId, streamProviderName, cts.Token);
            try
            {
                // generate events
                await GenerateEvents(streamProviderName, streamGuid, streamNamespace, eventsProduced);

                await observer.WaitForItemDeliveryCountAsync(streamId, subscription.SubscriptionId, eventsProduced, streamProviderName, cts.Token);
                Assert.Equal(eventsProduced, await consumer.GetNumberConsumed(cancellationToken));
            }
            finally
            {
                using var cleanup = new CancellationTokenSource(_timeout);
                await consumer.StopConsuming(cleanup.Token)
                    .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            }
        }

        private async Task GenerateEvents(string streamProviderName, Guid streamGuid, string streamNamespace, int produceCount)
        {
            IStreamProvider streamProvider = this.testHost.Client!.GetStreamProvider(streamProviderName); // The runner deploys the client.
            IAsyncObserver<int> observer = streamProvider.GetStream<int>(streamNamespace, streamGuid);
            for (int i = 0; i < produceCount; i++)
            {
                await observer.OnNextAsync(i);
            }
        }

        private async Task<Guid> ProduceEventsToClient(
            string streamProviderName,
            Guid streamGuid,
            string streamNamespace,
            int eventsProduced,
            int[] eventCount,
            CancellationToken cancellationToken)
        {
            using var observer = StreamingDiagnosticObserver.Create(testHost);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_timeout);
            var streamId = StreamId.Create(streamNamespace, streamGuid);

            var subscription = await SubscribeToStream(streamProviderName, streamGuid, streamNamespace,
                (e, t) =>
                {
                    eventCount[0]++;
                    return Task.CompletedTask;
                });

            var producer = this.testHost.GrainFactory!.GetGrain<ISampleStreaming_ProducerGrain>(Guid.NewGuid()); // The runner deploys the client.
            await producer.BecomeProducer(streamGuid, streamNamespace, streamProviderName, cts.Token);

            await ProduceExactCountAsync(producer, eventsProduced, cts.Token);
            await observer.WaitForItemDeliveryCountAsync(streamId, eventsProduced, streamProviderName, cts.Token);

            Assert.Equal(eventsProduced, eventCount[0]);
            Assert.Equal(eventsProduced, await producer.GetNumberProduced(cancellationToken));
            return subscription.HandleId;
        }

        private async Task WaitForDroppedClientsAsync(
            HashSet<ConnectedClient> droppedClients,
            GatewayDiagnosticObserver observer,
            CancellationToken cancellationToken)
        {
            if (droppedClients.Count == 0)
            {
                return;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(GetClientDropWaitTimeout());
            await observer.WaitForClientsDroppedAsync(droppedClients.Select(client => (client.SiloAddress, client.ClientId)), cts.Token);

            var remainingClients = GetConnectedClients();
            remainingClients.IntersectWith(droppedClients);
            Assert.Empty(remainingClients);
        }

        private HashSet<ConnectedClient> GetConnectedClients()
        {
            var result = new HashSet<ConnectedClient>();
            foreach (var silo in testHost.Silos)
            {
                var connectedClients = testHost.GetSiloServiceProvider(silo.SiloAddress).GetRequiredService<IConnectedClientCollection>();
                foreach (var clientId in connectedClients.GetConnectedClientIds())
                {
                    result.Add(new ConnectedClient(silo.SiloAddress, clientId));
                }
            }

            return result;
        }

        private TimeSpan GetClientDropWaitTimeout()
        {
            var maxDropTimeout = TimeSpan.Zero;
            foreach (var silo in testHost.Silos)
            {
                var options = testHost.GetSiloServiceProvider(silo.SiloAddress).GetRequiredService<IOptions<SiloMessagingOptions>>().Value;
                if (options.ClientDropTimeout > maxDropTimeout)
                {
                    maxDropTimeout = options.ClientDropTimeout;
                }
            }

            return (maxDropTimeout * 2) + TimeSpan.FromSeconds(10);
        }

        private static async Task ProduceExactCountAsync(
            ISampleStreaming_ProducerGrain producer,
            int count,
            CancellationToken cancellationToken)
        {
            for (var i = 0; i < count; i++)
            {
                await producer.Produce(cancellationToken);
            }
        }

        private readonly record struct ConnectedClient(SiloAddress SiloAddress, GrainId ClientId);
    }
}
