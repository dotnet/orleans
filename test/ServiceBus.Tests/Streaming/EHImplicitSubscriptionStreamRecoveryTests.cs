using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Streaming.EventHubs;
using Orleans.Providers.Streams.Generator;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.ServiceBus.Providers;
using Orleans.Streams;
using Orleans.TestingHost;
using Tester.StreamingTests;
using TestExtensions;
using TestGrains;
using UnitTests.Grains;
using Xunit;
using Microsoft.WindowsAzure.Storage.Table;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Hosting;
using Orleans;
using Microsoft.Extensions.Configuration;

namespace ServiceBus.Tests.StreamingTests
{
    [TestCategory("EventHub"), TestCategory("Streaming")]
    public class EHImplicitSubscriptionStreamRecoveryTests : OrleansTestingBase, IClassFixture<EHImplicitSubscriptionStreamRecoveryTests.Fixture>
    {
        private readonly Fixture fixture;
        private const string StreamProviderName = GeneratedStreamTestConstants.StreamProviderName;
        private const string EHPath = "ehorleanstest";
        private const string EHConsumerGroup = "orleansnightly";
        private const string EHCheckpointTable = "ehcheckpoint";
        private static readonly string CheckpointNamespace = Guid.NewGuid().ToString();

        private readonly ImplicitSubscritionRecoverableStreamTestRunner runner;

        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                // poor fault injection requires grain instances stay on same host, so only single host for this test
                builder.Options.InitialSilosCount = 1;

                builder.ConfigureLegacyConfiguration(legacy =>
                {
                    // register stream provider
                    legacy.ClusterConfiguration.AddMemoryStorageProvider("Default");
                });
                builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
                builder.AddClientBuilderConfigurator<MyClientBuilderConfigurator>();
            }

            public override void Dispose()
            {
                base.Dispose();
                var dataManager = new AzureTableDataManager<TableEntity>(EHCheckpointTable, TestDefaultConfiguration.DataConnectionString, NullLoggerFactory.Instance);
                dataManager.InitTableAsync().Wait();
                dataManager.ClearTableAsync().Wait();
            }

            private class MySiloBuilderConfigurator : ISiloBuilderConfigurator
            {
                public void Configure(ISiloHostBuilder hostBuilder)
                {
                    hostBuilder
                        .AddEventHubStreams(StreamProviderName,
                        options =>
                        {
                            options.ConnectionString = TestDefaultConfiguration.EventHubConnectionString;
                            options.ConsumerGroup = EHConsumerGroup;
                            options.Path = EHPath;
                            options.BalancerType = StreamQueueBalancerType.DynamicClusterConfigDeploymentBalancer;
                            options.PubSubType = StreamPubSubType.ImplicitOnly;
                            options.CheckpointConnectionString = TestDefaultConfiguration.DataConnectionString;
                            options.CheckpointTableName = EHCheckpointTable;
                            options.CheckpointNamespace = CheckpointNamespace;
                            options.CheckpointPersistInterval = TimeSpan.FromSeconds(1);
                        });
                }
            }

            private class MyClientBuilderConfigurator : IClientBuilderConfigurator
            {
                public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
                {
                    clientBuilder.AddEventHubStreams(StreamProviderName, options =>
                        {
                            options.ConnectionString = TestDefaultConfiguration.EventHubConnectionString;
                            options.ConsumerGroup = EHConsumerGroup;
                            options.Path = EHPath;
                            options.PubSubType = StreamPubSubType.ImplicitOnly;
                        });
                }
            }
        }

        public EHImplicitSubscriptionStreamRecoveryTests(Fixture fixture)
        {
            this.fixture = fixture;
            this.runner = new ImplicitSubscritionRecoverableStreamTestRunner(this.fixture.GrainFactory, StreamProviderName);
        }

        [Fact]
        public async Task Recoverable100EventStreamsWithTransientErrorsTest()
        {
            this.fixture.Logger.Info("************************ EHRecoverable100EventStreamsWithTransientErrorsTest *********************************");
            await runner.Recoverable100EventStreamsWithTransientErrors(GenerateEvents, ImplicitSubscription_TransientError_RecoverableStream_CollectorGrain.StreamNamespace, 4, 100);
        }

        [Fact]
        public async Task Recoverable100EventStreamsWith1NonTransientErrorTest()
        {
            this.fixture.Logger.Info("************************ EHRecoverable100EventStreamsWith1NonTransientErrorTest *********************************");
            await runner.Recoverable100EventStreamsWith1NonTransientError(GenerateEvents, ImplicitSubscription_NonTransientError_RecoverableStream_CollectorGrain.StreamNamespace, 4, 100);
        }

        private async Task GenerateEvents(string streamNamespace, int streamCount, int eventsInStream)
        {
            IStreamProvider streamProvider = this.fixture.HostedCluster.ServiceProvider.GetServiceByName<IStreamProvider>(StreamProviderName);
            IAsyncStream<GeneratedEvent>[] producers =
                Enumerable.Range(0, streamCount)
                    .Select(i => streamProvider.GetStream<GeneratedEvent>(Guid.NewGuid(), streamNamespace))
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
