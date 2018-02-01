using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans;
using Orleans.Providers.Streams.Common;
using Orleans.Providers.Streams.Generator;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.ServiceBus.Providers;
using Orleans.Streams;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using Orleans.Streaming.EventHubs;
using TestExtensions;
using TestGrainInterfaces;
using TestGrains;
using UnitTests.Grains;
using Xunit;
using Microsoft.WindowsAzure.Storage.Table;
using Microsoft.Extensions.Logging.Abstractions;

namespace ServiceBus.Tests.StreamingTests
{
    [TestCategory("EventHub"), TestCategory("Streaming")]
    public class EHStreamProviderCheckpointTests : TestClusterPerTest
    {
        private static readonly string StreamProviderTypeName = typeof(EventHubStreamProvider).FullName;
        private const string StreamProviderName = GeneratedStreamTestConstants.StreamProviderName;
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

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.ConfigureLegacyConfiguration(legacy =>
            {
                AdjustConfig(legacy.ClusterConfiguration);
                AdjustConfig(legacy.ClientConfiguration);
            });
        }

        [Fact]
        public async Task ReloadFromCheckpointTest()
        {
            logger.Info("************************ EHReloadFromCheckpointTest *********************************");
            await this.ReloadFromCheckpointTestRunner(ImplicitSubscription_RecoverableStream_CollectorGrain.StreamNamespace, 1, 256);
        }

        [Fact]
        public async Task RestartSiloAfterCheckpointTest()
        {
            logger.Info("************************ EHRestartSiloAfterCheckpointTest *********************************");
            await this.RestartSiloAfterCheckpointTestRunner(ImplicitSubscription_RecoverableStream_CollectorGrain.StreamNamespace, 8, 32);
        }

        public override void Dispose()
        {
            var dataManager = new AzureTableDataManager<TableEntity>(CheckpointerSettings.TableName, CheckpointerSettings.DataConnectionString, NullLoggerFactory.Instance);
            dataManager.InitTableAsync().Wait();
            dataManager.ClearTableAsync().Wait();
            base.Dispose();
        }

        private async Task ReloadFromCheckpointTestRunner(string streamNamespace, int streamCount, int eventsInStream)
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
                var reporter = this.GrainFactory.GetGrain<IGeneratedEventReporterGrain>(GeneratedStreamTestConstants.ReporterId);
                reporter.Reset().Ignore();
            }
        }

        private async Task RestartSiloAfterCheckpointTestRunner(string streamNamespace, int streamCount, int eventsInStream)
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
                var reporter = this.GrainFactory.GetGrain<IGeneratedEventReporterGrain>(GeneratedStreamTestConstants.ReporterId);
                reporter.Reset().Ignore();
            }
        }

        private async Task<bool> CheckCounters(string streamNamespace, int streamCount, int eventsInStream, bool assertIsTrue)
        {
            var reporter = this.GrainFactory.GetGrain<IGeneratedEventReporterGrain>(GeneratedStreamTestConstants.ReporterId);

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
            var mgmt = this.GrainFactory.GetGrain<IManagementGrain>(0);

            await mgmt.SendControlCommandToProvider(StreamProviderTypeName, StreamProviderName, (int)PersistentStreamProviderCommand.StopAgents);
            await mgmt.SendControlCommandToProvider(StreamProviderTypeName, StreamProviderName, (int)PersistentStreamProviderCommand.StartAgents);
        }

        private async Task GenerateEvents(string streamNamespace, List<Guid> streamGuids, int eventsInStream, int payloadSize)
        {
            IStreamProvider streamProvider = this.Client.GetStreamProvider(StreamProviderName);
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
            EventHubConfig.Value.WriteProperties(settings);
            CheckpointerSettings.WriteProperties(settings);

            // add queue balancer setting
            settings.Add(PersistentStreamProviderConfig.QUEUE_BALANCER_TYPE, StreamQueueBalancerType.DynamicClusterConfigDeploymentBalancer.AssemblyQualifiedName);

            // add pub/sub settting
            settings.Add(PersistentStreamProviderConfig.STREAM_PUBSUB_TYPE, StreamPubSubType.ImplicitOnly.ToString());
            return settings;
        }
    }
}
