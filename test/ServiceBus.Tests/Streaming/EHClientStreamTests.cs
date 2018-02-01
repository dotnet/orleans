using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.WindowsAzure.Storage.Table;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.ServiceBus.Providers;
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

        private static readonly Lazy<EventHubSettings> EventHubConfig = new Lazy<EventHubSettings>(() =>
            new EventHubSettings(
                TestDefaultConfiguration.EventHubConnectionString,
                EHConsumerGroup, EHPath));

        private static readonly EventHubStreamProviderSettings ProviderSettings =
            new EventHubStreamProviderSettings(StreamProviderName);

        private static readonly EventHubCheckpointerSettings CheckpointerSettings =
            new EventHubCheckpointerSettings(TestDefaultConfiguration.DataConnectionString, EHCheckpointTable,
                CheckpointNamespace,
                TimeSpan.FromSeconds(10));

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
                AdjustConfig(legacy.ClientConfiguration);
            });
        }

        public override void Dispose()
        {
            base.Dispose();
            var dataManager = new AzureTableDataManager<TableEntity>(CheckpointerSettings.TableName, CheckpointerSettings.DataConnectionString, NullLoggerFactory.Instance);
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
            config.Globals.RegisterStreamProvider<TestEventHubStreamProvider>(StreamProviderName, BuildProviderSettings());
            config.Globals.ClientDropTimeout = TimeSpan.FromSeconds(5);
        }

        private static void AdjustConfig(ClientConfiguration config)
        {
            config.RegisterStreamProvider<EventHubStreamProvider>(StreamProviderName, BuildProviderSettings());
        }

        private static Dictionary<string, string> BuildProviderSettings()
        {
            var settings = new Dictionary<string, string>();
            // get initial settings from configs
            ProviderSettings.WriteProperties(settings);
            EventHubConfig.Value.WriteProperties(settings);
            CheckpointerSettings.WriteProperties(settings);
            return settings;
        }
    }
}
