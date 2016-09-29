
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage.Table;
using Orleans.AzureUtils;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.ServiceBus.Providers;
using Orleans.TestingHost;
using Tester.TestStreamProviders;
using Tester.TestStreamProviders.EventHub;
using UnitTests.Tester;
using Xunit;
using Xunit.Abstractions;

namespace Tester.StreamingTests
{
    public class EHClientStreamTests : TestClusterPerTest
    {
        private const string StreamProviderName = "EventHubStreamProvider";
        private const string StreamNamespace = "StreamNamespace";
        private const string EHPath = "ehorleanstest";
        private const string EHConsumerGroup = "orleansnightly";
        private const string EHCheckpointTable = "ehcheckpoint";
        private static readonly string CheckpointNamespace = Guid.NewGuid().ToString();

        private static readonly EventHubSettings EventHubConfig = new EventHubSettings(StorageTestConstants.EventHubConnectionString,
                EHConsumerGroup, EHPath);

        private static readonly EventHubStreamProviderSettings ProviderSettings =
            new EventHubStreamProviderSettings(StreamProviderName) { CacheSizeMb = 3 };

        private static readonly EventHubCheckpointerSettings CheckpointerSettings =
            new EventHubCheckpointerSettings(StorageTestConstants.DataConnectionString, EHCheckpointTable,
                CheckpointNamespace,
                TimeSpan.FromSeconds(10));

        private readonly ITestOutputHelper output;
        private readonly ClientStreamTestRunner runner;

        public EHClientStreamTests(ITestOutputHelper output)
        {
            this.output = output;
            runner = new ClientStreamTestRunner(this.HostedCluster);
        }

        public override TestCluster CreateTestCluster()
        {
            var options = new TestClusterOptions(2);
            AdjustConfig(options.ClusterConfiguration);
            AdjustConfig(options.ClientConfiguration);
            return new TestCluster(options);
        }

        public override void Dispose()
        {
            base.Dispose();
            var dataManager = new AzureTableDataManager<TableEntity>(CheckpointerSettings.TableName, CheckpointerSettings.DataConnectionString);
            dataManager.InitTableAsync().Wait();
            dataManager.ClearTableAsync().Wait();
            TestAzureTableStorageStreamFailureHandler.DeleteAll().Wait();
        }

        [Fact, TestCategory("EventHub"), TestCategory("Streaming")]
        public async Task EHStreamProducerOnDroppedClientTest()
        {
            logger.Info("************************ EHStreamProducerOnDroppedClientTest *********************************");
            await runner.StreamProducerOnDroppedClientTest(StreamProviderName, StreamNamespace);
        }

        [Fact, TestCategory("EventHub"), TestCategory("Streaming")]
        public async Task EHStreamConsumerOnDroppedClientTest()
        {
            logger.Info("************************ EHStreamConsumerOnDroppedClientTest *********************************");
            await runner.StreamConsumerOnDroppedClientTest(StreamProviderName, StreamNamespace, output,
                    () => TestAzureTableStorageStreamFailureHandler.GetDeliveryFailureCount(StreamProviderName), true);
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
            EventHubConfig.WriteProperties(settings);
            CheckpointerSettings.WriteProperties(settings);
            return settings;
        }
    }
}
