
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans;
using Orleans.Providers.Streams.Generator;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using Tester;
using TestExtensions;
using TestGrainInterfaces;
using TestGrains;
using UnitTests.Grains;
using Xunit;

namespace UnitTests.StreamingTests
{
    public class ControllableStreamGeneratorProviderTests : OrleansTestingBase, IClassFixture<ControllableStreamGeneratorProviderTests.Fixture>
    {
        private readonly Fixture fixture;

        public class Fixture : BaseTestClusterFixture
        {
            public const string StreamProviderName = GeneratedStreamTestConstants.StreamProviderName;
            public static readonly string StreamProviderTypeName = typeof(GeneratorStreamProvider).FullName;
            public const string StreamNamespace = GeneratedEventCollectorGrain.StreamNamespace;

            public static readonly GeneratorAdapterConfig AdapterConfig = new GeneratorAdapterConfig(StreamProviderName)
            {
                TotalQueueCount = 4,
            };

            protected override TestCluster CreateTestCluster()
            {
                var options = new TestClusterOptions(2);
                var settings = new Dictionary<string, string>();
                // get initial settings from configs
                AdapterConfig.WriteProperties(settings);

                // add queue balancer setting
                settings.Add(PersistentStreamProviderConfig.QUEUE_BALANCER_TYPE, StreamQueueBalancerType.DynamicClusterConfigDeploymentBalancer.AssemblyQualifiedName);

                // add pub/sub settting
                settings.Add(PersistentStreamProviderConfig.STREAM_PUBSUB_TYPE, StreamPubSubType.ImplicitOnly.ToString());

                // register stream provider
                options.ClusterConfiguration.Globals.RegisterStreamProvider<GeneratorStreamProvider>(StreamProviderName, settings);
                return new TestCluster(options);
            }
        }

        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        public ControllableStreamGeneratorProviderTests(Fixture fixture)
        {
            this.fixture = fixture;
        }
        
        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Streaming")]
        public async Task ValidateControllableGeneratedStreamsTest()
        {
            this.fixture.Client.Logger.Info("************************ ValidateControllableGeneratedStreamsTest *********************************");
            await ValidateControllableGeneratedStreams();
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Streaming")]
        public async Task Validate2ControllableGeneratedStreamsTest()
        {
            this.fixture.Client.Logger.Info("************************ Validate2ControllableGeneratedStreamsTest *********************************");
            await ValidateControllableGeneratedStreams();
            await ValidateControllableGeneratedStreams();
        }

        public async Task ValidateControllableGeneratedStreams()
        {
            try
            {
                var generatorConfig = new SimpleGeneratorConfig
                {
                    StreamNamespace = Fixture.StreamNamespace,
                    EventsInStream = 100
                };

                var mgmt = this.fixture.GrainFactory.GetGrain<IManagementGrain>(0);
                object[] results = await mgmt.SendControlCommandToProvider(Fixture.StreamProviderTypeName, Fixture.StreamProviderName, (int)StreamGeneratorCommand.Configure, generatorConfig);
                Assert.Equal(2, results.Length);
                bool[] bResults = results.Cast<bool>().ToArray();

                foreach (var controlCommandResult in bResults)
                {
                    Assert.True(controlCommandResult);
                }

                await TestingUtils.WaitUntilAsync(assertIsTrue => CheckCounters(generatorConfig, assertIsTrue), Timeout);
            }
            finally
            {
                var reporter = this.fixture.GrainFactory.GetGrain<IGeneratedEventReporterGrain>(GeneratedStreamTestConstants.ReporterId);
                reporter.Reset().Ignore();
            }
        }

        private async Task<bool> CheckCounters(SimpleGeneratorConfig generatorConfig, bool assertIsTrue)
        {
            var reporter = this.fixture.GrainFactory.GetGrain<IGeneratedEventReporterGrain>(GeneratedStreamTestConstants.ReporterId);

            var report = await reporter.GetReport(GeneratedStreamTestConstants.StreamProviderName, GeneratedEventCollectorGrain.StreamNamespace);
            if (assertIsTrue)
            {
                // one stream per queue
                Assert.Equal(Fixture.AdapterConfig.TotalQueueCount, report.Count); // stream count
                foreach (int eventsPerStream in report.Values)
                {
                    Assert.Equal(generatorConfig.EventsInStream, eventsPerStream);
                }
            }
            else if (Fixture.AdapterConfig.TotalQueueCount != report.Count ||
                     report.Values.Any(count => count != generatorConfig.EventsInStream))
            {
                return false;
            }
            return true;
        }
    }
}
