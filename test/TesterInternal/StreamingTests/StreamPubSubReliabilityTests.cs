using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Storage;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.StorageTests;
using Xunit;

namespace UnitTests.StreamingTests
{
    public class StreamPubSubReliabilityTests : OrleansTestingBase, IClassFixture<StreamPubSubReliabilityTests.Fixture>, IAsyncLifetime
    {
        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.Options.InitialSilosCount = 4;
                builder.AddSiloBuilderConfigurator<SiloConfigurator>();
                builder.AddClientBuilderConfigurator<ClientConfiguretor>();
            }
        }

        public class SiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.AddMemoryStreams<DefaultMemoryMessageBodySerializer>(StreamTestsConstants.SMS_STREAM_PROVIDER_NAME)
                    .AddMemoryGrainStorage("MemoryStore", op => op.NumStorageGrains = 1)
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton<ErrorInjectionStorageProvider>();
                        services.AddSingletonNamedService<IGrainStorage, ErrorInjectionStorageProvider>(PubSubStoreProviderName);
                        services.AddSingletonNamedService<IControllable, ErrorInjectionStorageProvider>(PubSubStoreProviderName);
                    });
            }
        }
        public class ClientConfiguretor : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder.AddMemoryStreams<DefaultMemoryMessageBodySerializer>(StreamTestsConstants.SMS_STREAM_PROVIDER_NAME);
            }
        }

        private const string PubSubStoreProviderName = "PubSubStore";

        public IGrainFactory GrainFactory => _fixture.GrainFactory;

        protected Guid StreamId;
        protected string StreamProviderName;
        protected string StreamNamespace;
        protected TestCluster HostedCluster;
        private readonly Fixture _fixture;

        public StreamPubSubReliabilityTests(Fixture fixture)
        {
            StreamId = Guid.NewGuid();
            StreamProviderName = StreamTestsConstants.SMS_STREAM_PROVIDER_NAME;
            StreamNamespace = StreamTestsConstants.StreamLifecycleTestsNamespace;
            this.HostedCluster = fixture.HostedCluster;
            _fixture = fixture;
        }

        public async Task InitializeAsync()
        {
            await SetErrorInjection(PubSubStoreProviderName, ErrorInjectionPoint.None);
        }

        public Task DisposeAsync() => Task.CompletedTask;

        [Fact, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("PubSub")]
        public async Task PubSub_Store_Baseline()
        {
            await Test_PubSub_Stream(StreamProviderName, StreamId);
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("PubSub")]
        public async Task PubSub_Store_ReadError()
        {
            // Expected behaviour: Underlying error StorageProviderInjectedError returned to caller
            //
            // Actual behaviour: Rather cryptic error OrleansException returned, mentioning 
            //                   root cause problem "Failed SetupActivationState" in message text, 
            //                   but no more details or stack trace.

            await SetErrorInjection(PubSubStoreProviderName, ErrorInjectionPoint.BeforeRead);

            // TODO: expect StorageProviderInjectedError directly instead of OrleansException
            await Assert.ThrowsAsync<StorageProviderInjectedError>(() =>
                Test_PubSub_Stream(StreamProviderName, StreamId));
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("PubSub")]
        public async Task PubSub_Store_WriteError()
        {
            await SetErrorInjection(PubSubStoreProviderName, ErrorInjectionPoint.BeforeWrite);

            var exception = await Assert.ThrowsAsync<StorageProviderInjectedError>(() =>
                Test_PubSub_Stream(StreamProviderName, StreamId));
        }

        private async Task Test_PubSub_Stream(string streamProviderName, Guid streamId)
        {
            // Consumer
            IStreamLifecycleConsumerGrain consumer = this.GrainFactory.GetGrain<IStreamLifecycleConsumerGrain>(Guid.NewGuid());
            await consumer.BecomeConsumer(streamId, this.StreamNamespace, streamProviderName);

            // Producer
            IStreamLifecycleProducerGrain producer = this.GrainFactory.GetGrain<IStreamLifecycleProducerGrain>(Guid.NewGuid());
            await producer.BecomeProducer(StreamId, this.StreamNamespace, streamProviderName);

            await producer.SendItem(1);

            int received1 = 0;
            var cts = new CancellationTokenSource(1000);
            do
            {
                received1 = await consumer.GetReceivedCount();
            } while (received1 <= 1 || !cts.IsCancellationRequested);

            Assert.True(received1 > 1, $"Received count for consumer {consumer} is too low = {received1}");

            // Unsubscribe
            await consumer.ClearGrain();

            // Send one more message
            await producer.SendItem(2);

            await Task.Delay(300);

            int received2 = await consumer.GetReceivedCount();

            Assert.Equal(0, received2);  // $"Received count for consumer {consumer} is wrong = {received2}"

        }

        private async Task SetErrorInjection(string providerName, ErrorInjectionPoint errorInjectionPoint)
        {
            await ErrorInjectionStorageProvider.SetErrorInjection(
                providerName,
                new ErrorInjectionBehavior { ErrorInjectionPoint = errorInjectionPoint },
                this.HostedCluster.GrainFactory);
        }
    }
}