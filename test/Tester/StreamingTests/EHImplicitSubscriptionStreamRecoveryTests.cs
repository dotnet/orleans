
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Orleans;
using Orleans.ServiceBus.Providers;
using Orleans.Streams;
using Orleans.TestingHost;
using OrleansServiceBusUtils.Providers.Streams.EventHub;
using Tester;
using Tester.StreamingTests;
using TestGrains;
using UnitTests.Grains;
using UnitTests.Tester;
using Xunit;

namespace UnitTests.StreamingTests
{
    public class EHImplicitSubscriptionStreamRecoveryTestsFixture : BaseClusterFixture
    {
        public const string StreamProviderName = GeneratedStreamTestConstants.StreamProviderName;
        private const string EHPath = "ehorleanstest";
        private const string EHConsumerGroup = "orleansnightly";

        private static readonly EventHubSettings EventHubConfig = new EventHubSettings(StorageTestConstants.EventHubConnectionString, EHConsumerGroup, EHPath);

        private static readonly EventHubStreamProviderConfig ProviderConfig = new EventHubStreamProviderConfig(StreamProviderName);

        public EHImplicitSubscriptionStreamRecoveryTestsFixture()
            : base(new TestingSiloHost(
                new TestingSiloOptions
                {
                    StartFreshOrleans = true,
                    SiloConfigFile = new FileInfo("OrleansConfigurationForTesting.xml"),
                    AdjustConfig = config =>
                    {
                        // register stream provider
                        config.Globals.RegisterStreamProvider<EventHubStreamProvider>(StreamProviderName, BuildProviderSettings());

                        // Make sure a node config exist for each silo in the cluster.
                        // This is required for the DynamicClusterConfigDeploymentBalancer to properly balance queues.
                        config.GetOrCreateNodeConfigurationForSilo("Primary");
                        config.GetOrCreateNodeConfigurationForSilo("Secondary_1");
                    }
                }, new TestingClientOptions()
                {
                    AdjustConfig = config =>
                    {
                        config.RegisterStreamProvider<EventHubStreamProvider>(StreamProviderName, BuildProviderSettings());
                        config.Gateways.Add(new IPEndPoint(IPAddress.Loopback, 40001));
                    },
                }))
        {
        }

        private static Dictionary<string, string> BuildProviderSettings()
        {
            var settings = new Dictionary<string, string>();

            // get initial settings from configs
            ProviderConfig.WriteProperties(settings);
            EventHubConfig.WriteProperties(settings);

            // add queue balancer setting
            settings.Add(PersistentStreamProviderConfig.QUEUE_BALANCER_TYPE, StreamQueueBalancerType.DynamicClusterConfigDeploymentBalancer.ToString());

            // add pub/sub settting
            settings.Add(PersistentStreamProviderConfig.STREAM_PUBSUB_TYPE, StreamPubSubType.ImplicitOnly.ToString());
            return settings;
        }
    }

    public class EHImplicitSubscriptionStreamRecoveryTests :  OrleansTestingBase, IClassFixture<EHImplicitSubscriptionStreamRecoveryTestsFixture>
    {
        private static readonly string StreamProviderName = EHImplicitSubscriptionStreamRecoveryTestsFixture.StreamProviderName;

        private readonly ImplicitSubscritionRecoverableStreamTestRunner runner;

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
                    await producers[j].OnNextAsync(new GeneratedEvent {EventType = GeneratedEvent.GeneratedEventType.Fill});
                }
            }
            // send end events
            for (int j = 0; j < streamCount; j++)
            {
                await producers[j].OnNextAsync(new GeneratedEvent { EventType = GeneratedEvent.GeneratedEventType.End });
            }
        }
    }
}
