
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Xunit;
using Orleans;
using Orleans.Providers.Streams.Generator;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using TestGrainInterfaces;
using TestGrains;
using Tester;
using UnitTests.Grains;
using UnitTests.Tester;

namespace UnitTests.StreamingTests
{
    public class StreamGeneratorProviderTests : OrleansTestingBase, IClassFixture<StreamGeneratorProviderTests.Fixture>
    {
        private class Fixture : BaseTestClusterFixture
        {
            public const string StreamProviderName = GeneratedStreamTestConstants.StreamProviderName;
            public const string StreamNamespace = GeneratedEventCollectorGrain.StreamNamespace;

            public readonly static SimpleGeneratorConfig GeneratorConfig = new SimpleGeneratorConfig
            {
                StreamNamespace = StreamNamespace,
                EventsInStream = 100
            };

            public readonly static GeneratorAdapterConfig AdapterConfig = new GeneratorAdapterConfig(StreamProviderName)
            {
                TotalQueueCount = 4,
                GeneratorConfigType = GeneratorConfig.GetType()
            };

            protected override TestCluster CreateTestCluster()
            {
                var options = new TestClusterOptions(2);
                var settings = new Dictionary<string, string>();
                // get initial settings from configs
                AdapterConfig.WriteProperties(settings);
                GeneratorConfig.WriteProperties(settings);

                // add queue balancer setting
                settings.Add(PersistentStreamProviderConfig.QUEUE_BALANCER_TYPE, StreamQueueBalancerType.DynamicClusterConfigDeploymentBalancer.ToString());

                // add pub/sub settting
                settings.Add(PersistentStreamProviderConfig.STREAM_PUBSUB_TYPE, StreamPubSubType.ImplicitOnly.ToString());

                // register stream provider
                options.ClusterConfiguration.Globals.RegisterStreamProvider<GeneratorStreamProvider>(StreamProviderName, settings);
                return new TestCluster(options);
            }
        }

        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Streaming")]
        public async Task ValidateGeneratedStreamsTest()
        {
            logger.Info("************************ ValidateGeneratedStreamsTest *********************************");
            await TestingUtils.WaitUntilAsync(CheckCounters, Timeout);
        }

        private async Task<bool> CheckCounters(bool assertIsTrue)
        {
            var reporter = GrainClient.GrainFactory.GetGrain<IGeneratedEventReporterGrain>(GeneratedStreamTestConstants.ReporterId);

            var report = await reporter.GetReport(Fixture.StreamProviderName, Fixture.StreamNamespace);
            if (assertIsTrue)
            {
                // one stream per queue
                Assert.AreEqual(Fixture.AdapterConfig.TotalQueueCount, report.Count, "Stream count");
                foreach (int eventsPerStream in report.Values)
                {
                    Assert.AreEqual(Fixture.GeneratorConfig.EventsInStream, eventsPerStream, "Events per stream");
                }
            }
            else if (Fixture.AdapterConfig.TotalQueueCount != report.Count ||
                     report.Values.Any(count => count != Fixture.GeneratorConfig.EventsInStream))
            {
                return false;
            }
            return true;
        }
    }
}
