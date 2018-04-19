using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.WindowsAzure.Storage.Table;
using Orleans;
using Orleans.Providers.Streams.Common;
using Orleans.Providers.Streams.Generator;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using TestExtensions;
using TestGrainInterfaces;
using TestGrains;
using UnitTests.Grains;
using Xunit;
using Orleans.Hosting;
using Orleans.Configuration;

namespace ServiceBus.Tests.StreamingTests
{
    [TestCategory("EventHub"), TestCategory("Streaming")]
    public class EHStreamProviderCheckpointTests : TestClusterPerTest
    {
        private static readonly string StreamProviderTypeName = typeof(PersistentStreamProvider).FullName;
        private const string StreamProviderName = GeneratedStreamTestConstants.StreamProviderName;
        private const string EHPath = "ehorleanstest";
        private const string EHConsumerGroup = "orleansnightly";

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
            builder.AddClientBuilderConfigurator<MyClientBuilderConfigurator>();
        }

        private class MySiloBuilderConfigurator : ISiloBuilderConfigurator
        {
            public void Configure(ISiloHostBuilder hostBuilder)
            {
                hostBuilder
                    .AddAzureBlobGrainStorage(
                    ImplicitSubscription_RecoverableStream_CollectorGrain.StorageProviderName,
                    (AzureBlobStorageOptions options) =>
                    {
                        options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
                    })
                    .AddEventHubStreams(StreamProviderName, b=>b
                    .ConfigureEventHub(ob=>ob.Configure(
                    options =>
                    {
                        options.ConnectionString = TestDefaultConfiguration.EventHubConnectionString;
                        options.ConsumerGroup = EHConsumerGroup;
                        options.Path = EHPath;
                     
                    }))
                    .UseEventHubCheckpointer(ob=>ob.Configure(options => {
                        options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
                        options.PersistInterval = TimeSpan.FromSeconds(1);
                    }))
                    .UseDynamicClusterConfigDeploymentBalancer()
                    .ConfigureStreamPubSub(StreamPubSubType.ImplicitOnly));
            }
        }

        private class MyClientBuilderConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder
                    .AddEventHubStreams(StreamProviderName, b=>b
                    .ConfigureEventHub(ob => ob.Configure(
                    options =>
                    {
                        options.ConnectionString = TestDefaultConfiguration.EventHubConnectionString;
                        options.ConsumerGroup = EHConsumerGroup;
                        options.Path = EHPath;
                    }))
                    .ConfigureStreamPubSub(StreamPubSubType.ImplicitOnly));
            }
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
    }
}
