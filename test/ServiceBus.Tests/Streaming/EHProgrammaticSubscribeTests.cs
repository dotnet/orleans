using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.WindowsAzure.Storage.Table;
using Orleans.Runtime.Configuration;
using Orleans.ServiceBus.Providers;
using Orleans.ServiceBus.Providers.Testing;
using Orleans.Storage;
using Orleans.Streaming.EventHubs;
using Orleans.Streams;
using Orleans.TestingHost;
using ServiceBus.Tests.TestStreamProviders;
using Tester.StreamingTests;
using Tester.TestStreamProviders;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace ServiceBus.Tests.Streaming
{
    [TestCategory("EventHub"), TestCategory("Streaming")]
    public class EHProgrammaticSubscribeTest : ProgrammaticSubcribeTestsRunner, IClassFixture<EHProgrammaticSubscribeTest.Fixture>
    {
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

        public static readonly EventHubStreamProviderSettings ProviderSettings2 =
            new EventHubStreamProviderSettings(StreamProviderName2);

        public class Fixture : BaseTestClusterFixture
        {
            protected override TestCluster CreateTestCluster()
            {
                var options = new TestClusterOptions(2);
                AdjustClusterConfiguration(options.ClusterConfiguration);
                return new TestCluster(options);
            }

            private static Dictionary<string, string> BuildProviderSettings(EventHubStreamProviderSettings providerSettings)
            {
                var settings = new Dictionary<string, string>();
                // get initial settings from configs
                providerSettings.WriteProperties(settings);
                EventHubConfig.Value.WriteProperties(settings);
                CheckpointerSettings.WriteProperties(settings);
                return settings;
            }

            private static void AdjustClusterConfiguration(ClusterConfiguration config)
            {
                // register stream provider
                config.Globals.RegisterStreamProvider<EventHubStreamProvider>(StreamProviderName, BuildProviderSettings(ProviderSettings));
                config.Globals.RegisterStreamProvider<EventHubStreamProvider>(StreamProviderName2, BuildProviderSettings(ProviderSettings2));
                config.Globals.RegisterStorageProvider<MemoryStorage>("PubSubStore");
            }
        }

        public EHProgrammaticSubscribeTest(ITestOutputHelper output, Fixture fixture)
            : base(fixture)
        {
        }

        public override void Dispose()
        {
            base.Dispose();
            var dataManager = new AzureTableDataManager<TableEntity>(CheckpointerSettings.TableName, CheckpointerSettings.DataConnectionString, NullLoggerFactory.Instance);
            dataManager.InitTableAsync().Wait();
            dataManager.ClearTableAsync().Wait();
            TestAzureTableStorageStreamFailureHandler.DeleteAll().Wait();
        }
    }
}
