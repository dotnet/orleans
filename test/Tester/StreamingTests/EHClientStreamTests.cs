using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage.Table;
using Orleans.AzureUtils;
using Orleans.Runtime.Configuration;
using Orleans.ServiceBus.Providers;
using Orleans.TestingHost;
using UnitTests.Tester;
using Xunit;

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

        private static readonly EventHubStreamProviderConfig ProviderConfig =
            new EventHubStreamProviderConfig(StreamProviderName, 3);

        private static readonly EventHubCheckpointerSettings CheckpointerSettings =
            new EventHubCheckpointerSettings(StorageTestConstants.DataConnectionString, EHCheckpointTable,
                CheckpointNamespace,
                TimeSpan.FromSeconds(10));

        private ClientStreamTestRunner runner;

        public override TestCluster CreateTestCluster()
        {
            var options = new TestClusterOptions(2);
            AdjustConfig(options.ClusterConfiguration);
            AdjustConfig(options.ClientConfiguration);
            return new TestCluster(options);
        }

        public EHClientStreamTests()
        {
            runner = new ClientStreamTestRunner(this.HostedCluster);
        }

        public override void Dispose()
        {
            var dataManager = new AzureTableDataManager<TableEntity>(CheckpointerSettings.TableName, CheckpointerSettings.DataConnectionString);
            dataManager.InitTableAsync().Wait();
            dataManager.DeleteTableAsync().Wait();
            base.Dispose();
        }

        [Fact, TestCategory("EventHub"), TestCategory("Streaming")]
        public async Task EHStreamProducerOnDroppedClientTest()
        {
            logger.Info("************************ EHStreamProducerOnDroppedClientTest *********************************");
            await runner.StreamProducerOnDroppedClientTest(StreamProviderName, StreamNamespace);
        }
        
        private static void AdjustConfig(ClusterConfiguration config)
        {
            // register stream provider
            config.AddMemoryStorageProvider("PubSubStore");
            config.Globals.RegisterStreamProvider<EventHubStreamProvider>(StreamProviderName, BuildProviderSettings());
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
            ProviderConfig.WriteProperties(settings);
            EventHubConfig.WriteProperties(settings);
            CheckpointerSettings.WriteProperties(settings);
            return settings;
        }
    }
}
