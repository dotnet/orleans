using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage.Table;
using Orleans;
using Orleans.AzureUtils;
using Orleans.Providers.Streams.Common;
using Orleans.Providers.Streams.Generator;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.ServiceBus.Providers;
using Orleans.Streams;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using TestGrainInterfaces;
using TestGrains;
using UnitTests.Grains;
using UnitTests.Tester;
using Xunit;

namespace UnitTests.StreamingTests
{
    public class EHStreamProviderCheckpointTests : TestClusterPerTest
    {
        private static readonly string StreamProviderTypeName = typeof(EventHubStreamProvider).FullName;
        private const string StreamProviderName = GeneratedStreamTestConstants.StreamProviderName;
        private const string EHPath = "ehorleanstest";
        private const string EHConsumerGroup = "orleansnightly";
        private const string EHCheckpointTable = "ehcheckpoint";
        private static readonly string CheckpointNamespace = Guid.NewGuid().ToString();

        private static readonly EventHubSettings EventHubConfig = new EventHubSettings(StorageTestConstants.EventHubConnectionString,
            EHConsumerGroup, EHPath);

        private static readonly EventHubStreamProviderSettings ProviderSettings =
            new EventHubStreamProviderSettings(StreamProviderName) { CacheSizeMb = 3 };

        private static readonly EventHubCheckpointerSettings CheckpointerSettings =
            new EventHubCheckpointerSettings(StorageTestConstants.DataConnectionString, EHCheckpointTable, CheckpointNamespace,
                TimeSpan.FromSeconds(1));

        public override TestCluster CreateTestCluster()
        {
            var options = new TestClusterOptions(2);
            AdjustConfig(options.ClusterConfiguration);
            AdjustConfig(options.ClientConfiguration);
            return new TestCluster(options);
        }

        [Fact, TestCategory("EventHub"), TestCategory("Streaming")]
        public async Task ReloadFromCheckpointTest()
        {
            logger.Info("************************ EHReloadFromCheckpointTest *********************************");
            await ReloadFromCheckpointTest(ImplicitSubscription_RecoverableStream_CollectorGrain.StreamNamespace, 1, 256);
        }

        [Fact, TestCategory("EventHub"), TestCategory("Streaming")]
        public async Task RestartSiloAfterCheckpointTest()
        {
            logger.Info("************************ EHRestartSiloAfterCheckpointTest *********************************");
            await RestartSiloAfterCheckpointTest(ImplicitSubscription_RecoverableStream_CollectorGrain.StreamNamespace, 8, 32);
        }

        public override void Dispose()
        {
            var dataManager = new AzureTableDataManager<TableEntity>(CheckpointerSettings.TableName, CheckpointerSettings.DataConnectionString);
            dataManager.InitTableAsync().Wait();
            dataManager.ClearTableAsync().Wait();
            base.Dispose();
        }

        private async Task ReloadFromCheckpointTest(string streamNamespace, int streamCount, int eventsInStream)
        {
            List<Guid> streamGuids = Enumerable.Range(0, streamCount).Select(_ => Guid.NewGuid()).ToList();
            try
            {
                await GenerateEvents(streamNamespace, streamGuids, eventsInStream, 4096);
                await TestingUtils.WaitUntilAsync(assertIsTrue => CheckCounters(streamNamespace, streamCount, eventsInStream, assertIsTrue), TimeSpan.FromSeconds(60));

                await RestartAgents();

                await GenerateEvents(streamNamespace, streamGuids, eventsInStream, 4096);
                await TestingUtils.WaitUntilAsync(assertIsTrue => CheckCounters(streamNamespace, streamCount, eventsInStream * 2, assertIsTrue), TimeSpan.FromSeconds(90));
            }
            finally
            {
                var reporter = GrainClient.GrainFactory.GetGrain<IGeneratedEventReporterGrain>(GeneratedStreamTestConstants.ReporterId);
                reporter.Reset().Ignore();
            }
        }

        private async Task RestartSiloAfterCheckpointTest(string streamNamespace, int streamCount, int eventsInStream)
        {
            List<Guid> streamGuids = Enumerable.Range(0, streamCount).Select(_ => Guid.NewGuid()).ToList();
            try
            {
                await GenerateEvents(streamNamespace, streamGuids, eventsInStream, 0);
                await TestingUtils.WaitUntilAsync(assertIsTrue => CheckCounters(streamNamespace, streamCount, eventsInStream, assertIsTrue), TimeSpan.FromSeconds(60));

                HostedCluster.RestartSilo(HostedCluster.SecondarySilos[0]);
                await HostedCluster.WaitForLivenessToStabilizeAsync();

                await GenerateEvents(streamNamespace, streamGuids, eventsInStream, 0);
                await TestingUtils.WaitUntilAsync(assertIsTrue => CheckCounters(streamNamespace, streamCount, eventsInStream * 2, assertIsTrue), TimeSpan.FromSeconds(90));
            }
            finally
            {
                var reporter = GrainClient.GrainFactory.GetGrain<IGeneratedEventReporterGrain>(GeneratedStreamTestConstants.ReporterId);
                reporter.Reset().Ignore();
            }
        }

        private async Task<bool> CheckCounters(string streamNamespace, int streamCount, int eventsInStream, bool assertIsTrue)
        {
            var reporter = GrainClient.GrainFactory.GetGrain<IGeneratedEventReporterGrain>(GeneratedStreamTestConstants.ReporterId);

            var report = await reporter.GetReport(StreamProviderName, streamNamespace);
            if (assertIsTrue)
            {
                // one stream per queue
                Assert.Equal(streamCount, report.Count);
                foreach (int eventsPerStream in report.Values)
                {
                    Assert.Equal(eventsInStream, eventsPerStream);
                }
            }
            else if (streamCount != report.Count ||
                     report.Values.Any(count => count != eventsInStream))
            {
                return false;
            }
            return true;
        }

        private async Task RestartAgents()
        {
            var mgmt = GrainClient.GrainFactory.GetGrain<IManagementGrain>(0);

            await mgmt.SendControlCommandToProvider(StreamProviderTypeName, StreamProviderName, (int)PersistentStreamProviderCommand.StopAgents);
            await mgmt.SendControlCommandToProvider(StreamProviderTypeName, StreamProviderName, (int)PersistentStreamProviderCommand.StartAgents);
        }

        private async Task GenerateEvents(string streamNamespace, List<Guid> streamGuids, int eventsInStream, int payloadSize)
        {
            IStreamProvider streamProvider = GrainClient.GetStreamProvider(StreamProviderName);
            IAsyncStream<GeneratedEvent>[] producers = streamGuids
                    .Select(streamGuid => streamProvider.GetStream<GeneratedEvent>(streamGuid, streamNamespace))
                    .ToArray();

            for (int i = 0; i < eventsInStream - 1; i++)
            {
                // send event on each stream
                for (int j = 0; j < streamGuids.Count; j++)
                {
                    await producers[j].OnNextAsync(new GeneratedEvent { EventType = GeneratedEvent.GeneratedEventType.Fill, Payload = new int[payloadSize] });
                }
            }
            // send end events
            for (int j = 0; j < streamGuids.Count; j++)
            {
                await producers[j].OnNextAsync(new GeneratedEvent { EventType = GeneratedEvent.GeneratedEventType.Report, Payload = new int[payloadSize] });
            }
        }

        private static void AdjustConfig(ClusterConfiguration config)
        {
            // register stream provider
            config.Globals.RegisterStreamProvider<EventHubStreamProvider>(StreamProviderName, BuildProviderSettings());
            config.AddAzureTableStorageProvider(ImplicitSubscription_RecoverableStream_CollectorGrain.StorageProviderName);
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

            // add queue balancer setting
            settings.Add(PersistentStreamProviderConfig.QUEUE_BALANCER_TYPE, StreamQueueBalancerType.DynamicClusterConfigDeploymentBalancer.ToString());

            // add pub/sub settting
            settings.Add(PersistentStreamProviderConfig.STREAM_PUBSUB_TYPE, StreamPubSubType.ImplicitOnly.ToString());
            return settings;
        }
    }
}
