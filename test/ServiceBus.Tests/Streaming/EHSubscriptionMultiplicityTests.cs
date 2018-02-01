using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.WindowsAzure.Storage.Table;
using Orleans.Streaming.EventHubs;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.ServiceBus.Providers;
using Orleans.Storage;
using Orleans.Streams;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.StreamingTests;
using Xunit;

namespace ServiceBus.Tests.StreamingTests
{
    public class EHSubscriptionMultiplicityTests : OrleansTestingBase, IClassFixture<EHSubscriptionMultiplicityTests.Fixture>
    {
        private const string StreamProviderName = "EventHubStreamProvider";
        private const string StreamNamespace = "EHSubscriptionMultiplicityTestsNamespace";
        private const string EHPath = "ehorleanstest";
        private const string EHConsumerGroup = "orleansnightly";
        private const string EHCheckpointTable = "ehcheckpoint";
        private static readonly string CheckpointNamespace = Guid.NewGuid().ToString();

        public static readonly EventHubStreamProviderSettings ProviderSettings =
            new EventHubStreamProviderSettings(StreamProviderName);

        private static readonly Lazy<EventHubSettings> EventHubConfig = new Lazy<EventHubSettings>(() =>
            new EventHubSettings(
                TestDefaultConfiguration.EventHubConnectionString,
                EHConsumerGroup, EHPath));

        private static readonly EventHubCheckpointerSettings CheckpointerSettings =
            new EventHubCheckpointerSettings(TestDefaultConfiguration.DataConnectionString,
                EHCheckpointTable, CheckpointNamespace, TimeSpan.FromSeconds(1));

        private readonly SubscriptionMultiplicityTestRunner runner;
        private readonly Fixture fixture;

        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.ConfigureLegacyConfiguration(legacy =>
                {
                    AdjustClusterConfiguration(legacy.ClusterConfiguration);
                });
            }

            public override void Dispose()
            {
                base.Dispose();
                var dataManager = new AzureTableDataManager<TableEntity>(CheckpointerSettings.TableName, CheckpointerSettings.DataConnectionString, NullLoggerFactory.Instance);
                dataManager.InitTableAsync().Wait();
                dataManager.ClearTableAsync().Wait();
            }

            private static void AdjustClusterConfiguration(ClusterConfiguration config)
            {
                var settings = new Dictionary<string, string>();
                // get initial settings from configs
                ProviderSettings.WriteProperties(settings);
                EventHubConfig.Value.WriteProperties(settings);
                CheckpointerSettings.WriteProperties(settings);

                // add queue balancer setting
                settings.Add(PersistentStreamProviderConfig.QUEUE_BALANCER_TYPE, StreamQueueBalancerType.DynamicClusterConfigDeploymentBalancer.AssemblyQualifiedName);

                // register stream provider
                config.Globals.RegisterStreamProvider<EventHubStreamProvider>(StreamProviderName, settings);
                config.Globals.RegisterStorageProvider<MemoryStorage>("PubSubStore");
            }
        }

        public EHSubscriptionMultiplicityTests(Fixture fixture)
        {
            this.fixture = fixture;
            runner = new SubscriptionMultiplicityTestRunner(StreamProviderName, fixture.HostedCluster);            
        }

        [Fact, TestCategory("EventHub"), TestCategory("Streaming")]
        public async Task EHMultipleParallelSubscriptionTest()
        {
            this.fixture.Logger.Info("************************ EHMultipleParallelSubscriptionTest *********************************");
            await runner.MultipleParallelSubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [Fact, TestCategory("EventHub"), TestCategory("Streaming")]
        public async Task EHMultipleLinearSubscriptionTest()
        {
            this.fixture.Logger.Info("************************ EHMultipleLinearSubscriptionTest *********************************");
            await runner.MultipleLinearSubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [Fact, TestCategory("EventHub"), TestCategory("Streaming")]
        public async Task EHMultipleSubscriptionTest_AddRemove()
        {
            this.fixture.Logger.Info("************************ EHMultipleSubscriptionTest_AddRemove *********************************");
            await runner.MultipleSubscriptionTest_AddRemove(Guid.NewGuid(), StreamNamespace);
        }

        [Fact, TestCategory("EventHub"), TestCategory("Streaming")]
        public async Task EHResubscriptionTest()
        {
            this.fixture.Logger.Info("************************ EHResubscriptionTest *********************************");
            await runner.ResubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [Fact, TestCategory("EventHub"), TestCategory("Streaming")]
        public async Task EHResubscriptionAfterDeactivationTest()
        {
            this.fixture.Logger.Info("************************ EHResubscriptionAfterDeactivationTest *********************************");
            await runner.ResubscriptionAfterDeactivationTest(Guid.NewGuid(), StreamNamespace);
        }

        [Fact, TestCategory("EventHub"), TestCategory("Streaming")]
        public async Task EHActiveSubscriptionTest()
        {
            this.fixture.Logger.Info("************************ EHActiveSubscriptionTest *********************************");
            await runner.ActiveSubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [Fact, TestCategory("EventHub"), TestCategory("Streaming")]
        public async Task EHTwoIntermitentStreamTest()
        {
            this.fixture.Logger.Info("************************ EHTwoIntermitentStreamTest *********************************");
            await runner.TwoIntermitentStreamTest(Guid.NewGuid());
        }
    }
}
