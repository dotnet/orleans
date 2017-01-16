using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans;
using Orleans.Providers.Streams.Generator;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Streams;
using Orleans.TestingHost;
using Tester;
using Tester.StreamingTests;
using TestExtensions;
using TestGrains;
using UnitTests.Grains;
using Xunit;

namespace UnitTests.StreamingTests
{
    public class GeneratedImplicitSubscriptionStreamRecoveryTests : OrleansTestingBase, IClassFixture<GeneratedImplicitSubscriptionStreamRecoveryTests.Fixture>
    {
        private static readonly string StreamProviderTypeName = typeof(GeneratorStreamProvider).FullName;
        private readonly Fixture fixture;
        private readonly ImplicitSubscritionRecoverableStreamTestRunner runner;


        public class Fixture : BaseTestClusterFixture
        {
            public const string StreamProviderName = GeneratedStreamTestConstants.StreamProviderName;

            public readonly static GeneratorAdapterConfig AdapterConfig = new GeneratorAdapterConfig(StreamProviderName)
            {
                TotalQueueCount = 4,
            };

            protected override TestCluster CreateTestCluster()
            {
                var options = new TestClusterOptions(2);
                options.ClusterConfiguration.AddMemoryStorageProvider("MemoryStore");
                options.ClusterConfiguration.AddMemoryStorageProvider("Default");
                var settings = new Dictionary<string, string>();
                // get initial settings from configs
                AdapterConfig.WriteProperties(settings);

                // add queue balancer setting
                settings.Add(PersistentStreamProviderConfig.QUEUE_BALANCER_TYPE, StreamQueueBalancerType.DynamicClusterConfigDeploymentBalancer.ToString());

                // add pub/sub settting
                settings.Add(PersistentStreamProviderConfig.STREAM_PUBSUB_TYPE, StreamPubSubType.ImplicitOnly.ToString());

                // register stream provider
                options.ClusterConfiguration.Globals.RegisterStreamProvider<GeneratorStreamProvider>(StreamProviderName, settings);

                return new TestCluster(options);
            }
        }

        public GeneratedImplicitSubscriptionStreamRecoveryTests(Fixture fixture)
        {
            this.fixture = fixture;
            this.runner = new ImplicitSubscritionRecoverableStreamTestRunner(
                this.fixture.GrainFactory,
                Fixture.StreamProviderName);
        }

        [Fact, TestCategory("SlowBVT"), TestCategory("Functional"), TestCategory("Streaming")]
        public async Task Recoverable100EventStreamsWithTransientErrorsTest()
        {
            logger.Info("************************ Recoverable100EventStreamsWithTransientErrorsTest *********************************");
            await runner.Recoverable100EventStreamsWithTransientErrors(GenerateEvents,
                ImplicitSubscription_TransientError_RecoverableStream_CollectorGrain.StreamNamespace,
                Fixture.AdapterConfig.TotalQueueCount,
                100);
        }

        [Fact, TestCategory("SlowBVT"), TestCategory("Functional"), TestCategory("Streaming")]
        public async Task Recoverable100EventStreamsWith1NonTransientErrorTest()
        {
            logger.Info("************************ Recoverable100EventStreamsWith1NonTransientErrorTest *********************************");
            await runner.Recoverable100EventStreamsWith1NonTransientError(GenerateEvents,
                ImplicitSubscription_NonTransientError_RecoverableStream_CollectorGrain.StreamNamespace,
                Fixture.AdapterConfig.TotalQueueCount,
                100);
        }

        private async Task GenerateEvents(string streamNamespace, int streamCount, int eventsInStream)
        {
            var generatorConfig = new SimpleGeneratorConfig
            {
                StreamNamespace = streamNamespace,
                EventsInStream = eventsInStream
            };

            var mgmt = this.fixture.GrainFactory.GetGrain<IManagementGrain>(0);
            object[] results = await mgmt.SendControlCommandToProvider(StreamProviderTypeName, Fixture.StreamProviderName, (int)StreamGeneratorCommand.Configure, generatorConfig);
            Assert.Equal(2, results.Length);
            bool[] bResults = results.Cast<bool>().ToArray();
            foreach (var result in bResults)
            {
                Assert.True(result, "Control command result");
            }
        }
    }
}
