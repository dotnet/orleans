using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.WindowsAzure.Storage.Table;
using Microsoft.Extensions.Configuration;
using Orleans.Runtime;
using Orleans.Hosting;
using Orleans;
using Orleans.Configuration;
using Orleans.Runtime.Configuration;
using Orleans.Streaming.EventHubs;
using Orleans.TestingHost;
using Tester.TestStreamProviders;
using ServiceBus.Tests.TestStreamProviders.EventHub;
using Tester.StreamingTests;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace ServiceBus.Tests.StreamingTests
{
    [TestCategory("EventHub"), TestCategory("Streaming")]
    public class EHClientStreamTests : TestClusterPerTest
    {
        private const string StreamProviderName = "EventHubStreamProvider";
        private const string StreamNamespace = "StreamNamespace";
        private const string EHPath = "ehorleanstest";
        private const string EHConsumerGroup = "orleansnightly";
        private const string EHCheckpointTable = "ehcheckpoint";
        private static readonly string CheckpointNamespace = Guid.NewGuid().ToString();

        private readonly ITestOutputHelper output;
        private readonly ClientStreamTestRunner runner;
        public EHClientStreamTests(ITestOutputHelper output)
        {
            this.output = output;
            runner = new ClientStreamTestRunner(this.HostedCluster);
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.ConfigureLegacyConfiguration(legacy =>
            {
                AdjustConfig(legacy.ClusterConfiguration);
            });
            builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
            builder.AddClientBuilderConfigurator<MyClientBuilderConfigurator>();
        }

        private class MySiloBuilderConfigurator : ISiloBuilderConfigurator
        {
            public void Configure(ISiloHostBuilder hostBuilder)
            {
                hostBuilder
                    .AddPersistentStreams<EventHubStreamOptions>(StreamProviderName, TestEventHubStreamAdapterFactory.Create,
                    options =>
                    {
                        options.ConnectionString = TestDefaultConfiguration.EventHubConnectionString;
                        options.ConsumerGroup = EHConsumerGroup;
                        options.Path = EHPath;
                        options.CheckpointConnectionString = TestDefaultConfiguration.DataConnectionString;
                        options.CheckpointTableName = EHCheckpointTable;
                        options.CheckpointNamespace = CheckpointNamespace;
                        options.CheckpointPersistInterval = TimeSpan.FromSeconds(10);
                    });
            }
        }

        private class MyClientBuilderConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder
                    .AddPersistentStreams<EventHubStreamOptions>(StreamProviderName, TestEventHubStreamAdapterFactory.Create,
                    options =>
                    {
                        options.ConnectionString = TestDefaultConfiguration.EventHubConnectionString;
                        options.ConsumerGroup = EHConsumerGroup;
                        options.Path = EHPath;
                    });
            }
        }

        public override void Dispose()
        {
            base.Dispose();
            var dataManager = new AzureTableDataManager<TableEntity>(EHCheckpointTable, TestDefaultConfiguration.DataConnectionString, NullLoggerFactory.Instance);
            dataManager.InitTableAsync().Wait();
            dataManager.ClearTableAsync().Wait();
            TestAzureTableStorageStreamFailureHandler.DeleteAll().Wait();
        }

        [Fact]
        public async Task EHStreamProducerOnDroppedClientTest()
        {
            logger.Info("************************ EHStreamProducerOnDroppedClientTest *********************************");
            await runner.StreamProducerOnDroppedClientTest(StreamProviderName, StreamNamespace);
        }

        [Fact]
        public async Task EHStreamConsumerOnDroppedClientTest()
        {
            logger.Info("************************ EHStreamConsumerOnDroppedClientTest *********************************");
            await runner.StreamConsumerOnDroppedClientTest(StreamProviderName, StreamNamespace, output,
                    () => TestAzureTableStorageStreamFailureHandler.GetDeliveryFailureCount(StreamProviderName, NullLoggerFactory.Instance), true);
        }

        private static void AdjustConfig(ClusterConfiguration config)
        {
            // register stream provider
            config.AddMemoryStorageProvider("PubSubStore");
            config.Globals.ClientDropTimeout = TimeSpan.FromSeconds(5);
        }
    }
}
