
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.ServiceBus.Providers;
using Orleans.Streams;
using Orleans.TestingHost;
using OrleansServiceBusUtils.Providers.Streams.EventHub;
using Tester.StreamingTests;
using TestGrains;
using UnitTests.Grains;
using UnitTests.Tester;

namespace UnitTests.StreamingTests
{
    [DeploymentItem("OrleansConfigurationForTesting.xml")]
    [DeploymentItem("OrleansProviders.dll")]
    [TestClass]
    public class EHImplicitSubscriptionStreamRecoveryTests : HostedTestClusterPerFixture
    {
        private const string StreamProviderName = GeneratedStreamTestConstants.StreamProviderName;
        private const string EHPath = "ehorleanstest";
        private const string EHConsumerGroup = "orleansnightly";

        private static readonly EventHubSettings EventHubConfig = new EventHubSettings(StorageTestConstants.EventHubConnectionString,
            EHConsumerGroup, EHPath);

        private static readonly EventHubStreamProviderConfig ProviderConfig =
            new EventHubStreamProviderConfig(StreamProviderName);

        private ImplicitSubscritionRecoverableStreamTestRunner runner;

        public static TestingSiloHost CreateSiloHost()
        {
            return new TestingSiloHost(
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
                        config.GetOrAddConfigurationForNode("Primary");
                        config.GetOrAddConfigurationForNode("Secondary_1");
                    }
                }, new TestingClientOptions()
                {
                    AdjustConfig = config =>
                    {
                        config.RegisterStreamProvider<EventHubStreamProvider>(StreamProviderName, BuildProviderSettings());
                        config.Gateways.Add(new IPEndPoint(IPAddress.Loopback, 40001));
                    },
                });
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

        [TestInitialize] 
        public void InitializeOrleans()
        {
            runner = new ImplicitSubscritionRecoverableStreamTestRunner(GrainClient.GrainFactory, StreamProviderName);
        }


        [TestMethod, TestCategory("EventHub"), TestCategory("Streaming")]
        public async Task Recoverable100EventStreamsWithTransientErrorsTest()
        {
            logger.Info("************************ EHRecoverable100EventStreamsWithTransientErrorsTest *********************************");
            await runner.Recoverable100EventStreamsWithTransientErrors(GenerateEvents, ImplicitSubscription_TransientError_RecoverableStream_CollectorGrain.StreamNamespace, 4, 100);
        }

        [TestMethod, TestCategory("EventHub"), TestCategory("Streaming")]
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
