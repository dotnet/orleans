
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage.Table;
using Orleans;
using Orleans.AzureUtils;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.ServiceBus.Providers;
using Orleans.Streams;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using Tester;
using ServiceBus.Tests.TestStreamProviders.EventHub;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace ServiceBus.Tests.StreamingTests
{
    [TestCategory("EventHub"), TestCategory("Streaming")]
    public class EHStreamPerPartitionTests : OrleansTestingBase, IClassFixture<EHStreamPerPartitionTests.Fixture>
    {
        private readonly Fixture fixture;
        private const string StreamProviderName = "EHStreamPerPartition";
        private const string EHPath = "ehorleanstest";
        private const string EHConsumerGroup = "orleansnightly";
        private const string EHCheckpointTable = "ehcheckpoint";
        private static readonly string CheckpointNamespace = Guid.NewGuid().ToString();

        private static readonly Lazy<EventHubSettings> EventHubConfig = new Lazy<EventHubSettings>(() =>
            new EventHubSettings(
                TestDefaultConfiguration.EventHubConnectionString,
                EHConsumerGroup, EHPath));

        private static readonly EventHubCheckpointerSettings CheckpointerSettings =
            new EventHubCheckpointerSettings(TestDefaultConfiguration.DataConnectionString,
                EHCheckpointTable, CheckpointNamespace, TimeSpan.FromSeconds(1));

        private static readonly EventHubStreamProviderSettings ProviderSettings =
            new EventHubStreamProviderSettings(StreamProviderName);

        public class Fixture : BaseTestClusterFixture
        {
            protected override TestCluster CreateTestCluster()
            {
                var options = new TestClusterOptions(2);
                // register stream provider
                options.ClusterConfiguration.AddMemoryStorageProvider("PubSubStore");
                options.ClusterConfiguration.Globals.RegisterStreamProvider<StreamPerPartitionEventHubStreamProvider>(StreamProviderName, BuildProviderSettings());
                options.ClientConfiguration.RegisterStreamProvider<EventHubStreamProvider>(StreamProviderName, BuildProviderSettings());
                return new TestCluster(options);
            }

            public override void Dispose()
            {
                base.Dispose();
                var dataManager = new AzureTableDataManager<TableEntity>(CheckpointerSettings.TableName, CheckpointerSettings.DataConnectionString);
                dataManager.InitTableAsync().Wait();
                dataManager.ClearTableAsync().Wait();
            }

            private static Dictionary<string, string> BuildProviderSettings()
            {
                var settings = new Dictionary<string, string>();

                // get initial settings from configs
                ProviderSettings.WriteProperties(settings);
                EventHubConfig.Value.WriteProperties(settings);
                CheckpointerSettings.WriteProperties(settings);

                // add queue balancer setting
                settings.Add(PersistentStreamProviderConfig.QUEUE_BALANCER_TYPE, StreamQueueBalancerType.StaticClusterConfigDeploymentBalancer.AssemblyQualifiedName);

                return settings;
            }
        }

        public EHStreamPerPartitionTests(Fixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact]
        public async Task EH100StreamsTo4PartitionStreamsTest()
        {
            this.fixture.Logger.Info("************************ EH100StreamsTo4PartitionStreamsTest *********************************");

            int streamCount = 100;
            int eventsInStream = 10;
            int partitionCount = 4;

            List<ISampleStreaming_ConsumerGrain> consumers = new List<ISampleStreaming_ConsumerGrain>(partitionCount);
            for (int i = 0; i < partitionCount; i++)
            {
                consumers.Add(this.fixture.GrainFactory.GetGrain<ISampleStreaming_ConsumerGrain>(Guid.NewGuid()));
            }

            // subscribe to each partition
            List<Task> becomeConsumersTasks = consumers
                .Select( (consumer, i) => consumer.BecomeConsumer( StreamPerPartitionEventHubStreamProvider.GetPartitionGuid(i.ToString()), null, StreamProviderName))
                .ToList();
            await Task.WhenAll(becomeConsumersTasks);

            await GenerateEvents(streamCount, eventsInStream);
            await TestingUtils.WaitUntilAsync(assertIsTrue => CheckCounters(consumers, streamCount * eventsInStream, assertIsTrue), TimeSpan.FromSeconds(30));
        }

        private async Task GenerateEvents(int streamCount, int eventsInStream)
        {
            IStreamProvider streamProvider = this.fixture.Client.GetStreamProvider(StreamProviderName);
            IAsyncStream<int>[] producers =
                Enumerable.Range(0, streamCount)
                    .Select(i => streamProvider.GetStream<int>(Guid.NewGuid(), null))
                    .ToArray();

            for (int i = 0; i < eventsInStream; i++)
            {
                // send event on each stream
                for (int j = 0; j < streamCount; j++)
                {
                    await producers[j].OnNextAsync(i);
                }
            }
        }

        private async Task<bool> CheckCounters(List<ISampleStreaming_ConsumerGrain> consumers, int totalEventCount, bool assertIsTrue)
        {
            List<Task<int>> becomeConsumersTasks = consumers
                .Select((consumer, i) => consumer.GetNumberConsumed())
                .ToList();
            int[] counts = await Task.WhenAll(becomeConsumersTasks);

            if (assertIsTrue)
            {
                // one stream per queue
                Assert.Equal(totalEventCount, counts.Sum());
            }
            else if (totalEventCount != counts.Sum())
            {
                return false;
            }
            return true;
        }
    }
}
