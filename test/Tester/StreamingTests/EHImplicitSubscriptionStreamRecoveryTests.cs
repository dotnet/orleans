
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage.Table;
using Orleans;
using Orleans.AzureUtils;
using Orleans.Providers.Streams.Generator;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.ServiceBus.Providers;
using Orleans.Streams;
using Orleans.TestingHost;
using Tester;
using Tester.StreamingTests;
using TestGrains;
using UnitTests.Grains;
using UnitTests.Tester;
using Xunit;

namespace UnitTests.StreamingTests
{
    public class EHImplicitSubscriptionStreamRecoveryTests : OrleansTestingBase, IClassFixture<EHImplicitSubscriptionStreamRecoveryTests.Fixture>
    {
        private const string StreamProviderName = GeneratedStreamTestConstants.StreamProviderName;
        private const string EHPath = "ehorleanstest";
        private const string EHConsumerGroup = "orleansnightly";
        private const string EHCheckpointTable = "ehcheckpoint";
        private static readonly string CheckpointNamespace = Guid.NewGuid().ToString();

        private static readonly EventHubSettings EventHubConfig = new EventHubSettings(StorageTestConstants.EventHubConnectionString,
                EHConsumerGroup, EHPath);

        private static readonly EventHubStreamProviderSettings ProviderSettings =
            new EventHubStreamProviderSettings(StreamProviderName);

        private static readonly EventHubCheckpointerSettings CheckpointerSettings =
            new EventHubCheckpointerSettings(StorageTestConstants.DataConnectionString, EHCheckpointTable, CheckpointNamespace,
                TimeSpan.FromSeconds(1));

        private readonly ImplicitSubscritionRecoverableStreamTestRunner runner;

        private class Fixture : BaseTestClusterFixture
        {
            protected override TestCluster CreateTestCluster()
            {
                // poor fault injection requires grain instances stay on same host, so only single host for this test
                var options = new TestClusterOptions(1);
                // register stream provider
                options.ClusterConfiguration.AddMemoryStorageProvider("Default");
                options.ClusterConfiguration.Globals.RegisterStreamProvider<EventHubStreamProvider>(StreamProviderName, BuildProviderSettings());
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
                EventHubConfig.WriteProperties(settings);
                CheckpointerSettings.WriteProperties(settings);

                // add queue balancer setting
                settings.Add(PersistentStreamProviderConfig.QUEUE_BALANCER_TYPE, StreamQueueBalancerType.DynamicClusterConfigDeploymentBalancer.ToString());

                // add pub/sub settting
                settings.Add(PersistentStreamProviderConfig.STREAM_PUBSUB_TYPE, StreamPubSubType.ImplicitOnly.ToString());
                return settings;
            }
        }

        public EHImplicitSubscriptionStreamRecoveryTests()
        {
            runner = new ImplicitSubscritionRecoverableStreamTestRunner(GrainClient.GrainFactory, StreamProviderName);
        }

        [Fact, TestCategory("EventHub"), TestCategory("Streaming")]
        public async Task Recoverable100EventStreamsWithTransientErrorsTest()
        {
            logger.Info("************************ EHRecoverable100EventStreamsWithTransientErrorsTest *********************************");
            await runner.Recoverable100EventStreamsWithTransientErrors(GenerateEvents, ImplicitSubscription_TransientError_RecoverableStream_CollectorGrain.StreamNamespace, 4, 100);
        }

        [Fact, TestCategory("EventHub"), TestCategory("Streaming")]
        public async Task Recoverable100EventStreamsWith1NonTransientErrorTest()
        {
            logger.Info("************************ EHRecoverable100EventStreamsWith1NonTransientErrorTest *********************************");
            await runner.Recoverable100EventStreamsWith1NonTransientError(GenerateEvents, ImplicitSubscription_NonTransientError_RecoverableStream_CollectorGrain.StreamNamespace, 4, 100);
        }

        private async Task GenerateEvents(string streamNamespace, int streamCount, int eventsInStream)
        {
            IStreamProvider streamProvider = GrainClient.GetStreamProvider(StreamProviderName);
            IAsyncStream<GeneratedEvent>[] producers =
                Enumerable.Range(0, streamCount)
                    .Select(i => streamProvider.GetStream<GeneratedEvent>(Guid.NewGuid(), streamNamespace))
                    .ToArray();

            for (int i = 0; i < eventsInStream - 1; i++)
            {
                // send event on each stream
                for (int j = 0; j < streamCount; j++)
                {
                    await producers[j].OnNextAsync(new GeneratedEvent { EventType = GeneratedEvent.GeneratedEventType.Fill });
                }
            }
            // send end events
            for (int j = 0; j < streamCount; j++)
            {
                await producers[j].OnNextAsync(new GeneratedEvent { EventType = GeneratedEvent.GeneratedEventType.Report });
            }
        }
    }
}
