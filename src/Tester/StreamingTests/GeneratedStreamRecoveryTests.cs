    
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.TestingHost;
using Tester.StreamingTests;
using Tester.TestStreamProviders.Generator;
using Tester.TestStreamProviders.Generator.Generators;
using TestGrains;
using UnitTests.Grains;
using UnitTests.Tester;

namespace UnitTests.StreamingTests
{
    [DeploymentItem("OrleansConfigurationForTesting.xml")]
    [DeploymentItem("OrleansProviders.dll")]
    [TestClass]
    public class GeneratedImplicitSubscriptionStreamRecoveryTests : HostedTestClusterPerFixture
    {
        private static readonly string StreamProviderTypeName = typeof(GeneratorStreamProvider).FullName;
        private const string StreamProviderName = GeneratedStreamTestConstants.StreamProviderName;

        private readonly static GeneratorAdapterConfig AdapterConfig = new GeneratorAdapterConfig(StreamProviderName)
        {
            TotalQueueCount = 4,
        };

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
                        var settings = new Dictionary<string, string>();
                        // get initial settings from configs
                        AdapterConfig.WriteProperties(settings);

                        // add queue balancer setting
                        settings.Add(PersistentStreamProviderConfig.QUEUE_BALANCER_TYPE, StreamQueueBalancerType.DynamicClusterConfigDeploymentBalancer.ToString());

                        // add pub/sub settting
                        settings.Add(PersistentStreamProviderConfig.STREAM_PUBSUB_TYPE, StreamPubSubType.ImplicitOnly.ToString());

                        // register stream provider
                        config.Globals.RegisterStreamProvider<GeneratorStreamProvider>(StreamProviderName, settings);

                        // Make sure a node config exist for each silo in the cluster.
                        // This is required for the DynamicClusterConfigDeploymentBalancer to properly balance queues.
                        // GetOrAddConfigurationForNode will materialize a node in the configuration for each silo, if one does not already exist.
                        config.GetOrAddConfigurationForNode("Primary");
                        config.GetOrAddConfigurationForNode("Secondary_1");
                    }
                });
        }

        [TestInitialize]
        public void InitializeOrleans()
        {
            runner = new ImplicitSubscritionRecoverableStreamTestRunner(GrainClient.GrainFactory, StreamProviderName);
        }


        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Streaming")]
        public async Task Recoverable100EventStreamsWithTransientErrorsTest()
        {
            logger.Info("************************ Recoverable100EventStreamsWithTransientErrorsTest *********************************");
            await runner.Recoverable100EventStreamsWithTransientErrors(GenerateEvents, 
                ImplicitSubscription_TransientError_RecoverableStream_CollectorGrain.StreamNamespace,
                AdapterConfig.TotalQueueCount, 100);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Streaming")]
        public async Task Recoverable100EventStreamsWith1NonTransientErrorTest()
        {
            logger.Info("************************ Recoverable100EventStreamsWith1NonTransientErrorTest *********************************");
            await runner.Recoverable100EventStreamsWith1NonTransientError(GenerateEvents,
                ImplicitSubscription_NonTransientError_RecoverableStream_CollectorGrain.StreamNamespace,
                AdapterConfig.TotalQueueCount, 100);
        }

        private async Task GenerateEvents(string streamNamespace, int streamCount, int eventsInStream)
        {
            var generatorConfig = new SimpleGeneratorConfig
            {
                StreamNamespace = streamNamespace,
                EventsInStream = eventsInStream
            };

            var mgmt = GrainClient.GrainFactory.GetGrain<IManagementGrain>(0);
            object[] results = await mgmt.SendControlCommandToProvider(StreamProviderTypeName, StreamProviderName, (int)StreamGeneratorCommand.Configure, generatorConfig);
            Assert.AreEqual(2, results.Length, "expected responses");
            bool[] bResults = results.Cast<bool>().ToArray();
            foreach (var result in bResults)
            {
                Assert.AreEqual(true, result, "Control command result");
            }
        }
    }
}
