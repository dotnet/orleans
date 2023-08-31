using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Orleans.Providers.Streams.Generator;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.TestingHost;
using Tester.StreamingTests;
using TestExtensions;
using TestGrains;
using UnitTests.Grains;
using Xunit;

namespace ServiceBus.Tests.StreamingTests
{
    [TestCategory("EventHub"), TestCategory("Streaming"), TestCategory("Functional")]
    public class EHImplicitSubscriptionStreamRecoveryTests : OrleansTestingBase, IClassFixture<EHImplicitSubscriptionStreamRecoveryTests.Fixture>
    {
        private readonly Fixture fixture;
        private const string StreamProviderName = GeneratedStreamTestConstants.StreamProviderName;
        private const string EHPath = "ehorleanstest2";
        private const string EHConsumerGroup = "orleansnightly";

        private readonly ImplicitSubscritionRecoverableStreamTestRunner runner;

        public class Fixture : BaseEventHubTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                // poor fault injection requires grain instances stay on same host, so only single host for this test
                builder.Options.InitialSilosCount = 1;
                builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
                builder.AddClientBuilderConfigurator<MyClientBuilderConfigurator>();
            }

            private class MySiloBuilderConfigurator : ISiloConfigurator
            {
                public void Configure(ISiloBuilder hostBuilder)
                {
                    hostBuilder
                        .AddEventHubStreams(StreamProviderName, b=>
                        {
                            b.ConfigureEventHub(ob => ob.Configure(options =>
                            {
                                options.ConfigureTestDefaults(EHPath, EHConsumerGroup);
                            }));
                            b.UseAzureTableCheckpointer(ob => ob.Configure(options =>
                            {
                                options.ConfigureTableServiceClient(TestDefaultConfiguration.DataConnectionString);
                                options.PersistInterval = TimeSpan.FromSeconds(1);
                            }));
                            b.UseDynamicClusterConfigDeploymentBalancer();
                            b.ConfigureStreamPubSub(StreamPubSubType.ImplicitOnly);
                        });
                    hostBuilder
                        .AddMemoryGrainStorageAsDefault();
                }
            }

            private class MyClientBuilderConfigurator : IClientBuilderConfigurator
            {
                public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
                {
                    clientBuilder.AddEventHubStreams(StreamProviderName, b=>
                    {
                        b.ConfigureStreamPubSub(StreamPubSubType.ImplicitOnly);
                        b.ConfigureEventHub(ob => ob.Configure(options =>
                         {
                             options.ConfigureTestDefaults(EHPath, EHConsumerGroup);
                         }));
                    });
                }
            }
        }

        public EHImplicitSubscriptionStreamRecoveryTests(Fixture fixture)
        {
            this.fixture = fixture;
            fixture.EnsurePreconditionsMet();
            this.runner = new ImplicitSubscritionRecoverableStreamTestRunner(this.fixture.GrainFactory, StreamProviderName);
        }

        [SkippableFact(Skip="https://github.com/dotnet/orleans/issues/5633")]
        public async Task Recoverable100EventStreamsWithTransientErrorsTest()
        {
            this.fixture.Logger.LogInformation("************************ EHRecoverable100EventStreamsWithTransientErrorsTest *********************************");
            await runner.Recoverable100EventStreamsWithTransientErrors(GenerateEvents, ImplicitSubscription_TransientError_RecoverableStream_CollectorGrain.StreamNamespace, 4, 100);
        }

        [SkippableFact(Skip= "https://github.com/dotnet/orleans/issues/5638")]
        public async Task Recoverable100EventStreamsWith1NonTransientErrorTest()
        {
            this.fixture.Logger.LogInformation("************************ EHRecoverable100EventStreamsWith1NonTransientErrorTest *********************************");
            await runner.Recoverable100EventStreamsWith1NonTransientError(GenerateEvents, ImplicitSubscription_NonTransientError_RecoverableStream_CollectorGrain.StreamNamespace, 4, 100);
        }

        private async Task GenerateEvents(string streamNamespace, int streamCount, int eventsInStream)
        {
            IStreamProvider streamProvider = this.fixture.HostedCluster.ServiceProvider.GetServiceByName<IStreamProvider>(StreamProviderName);
            IAsyncStream<GeneratedEvent>[] producers =
                Enumerable.Range(0, streamCount)
                    .Select(i => streamProvider.GetStream<GeneratedEvent>(streamNamespace, Guid.NewGuid()))
                    .ToArray();

            for (int i = 0; i < eventsInStream - 1; i++)
            {
                // send event on each stream
                for (int j = 0; j < streamCount; j++)
                {
                    await producers[j].OnNextAsync(new GeneratedEvent { EventType = GeneratedEvent.GeneratedEventType.Fill });
                }
            }
            // send end events
            for (int j = 0; j < streamCount; j++)
            {
                await producers[j].OnNextAsync(new GeneratedEvent { EventType = GeneratedEvent.GeneratedEventType.Report });
            }
        }
    }
}
