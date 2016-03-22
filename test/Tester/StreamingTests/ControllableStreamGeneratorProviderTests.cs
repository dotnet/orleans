
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.TestingHost;
using Tester.TestStreamProviders.Generator;
using Tester.TestStreamProviders.Generator.Generators;
using TestGrainInterfaces;
using TestGrains;
using Tester;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Xunit;
using UnitTests.Grains;
using UnitTests.Tester;

namespace UnitTests.StreamingTests
{
    public class ControllableStreamGeneratorProviderTests : OrleansTestingBase, IClassFixture<ControllableStreamGeneratorProviderTests.Fixture>
    {
        private class Fixture : BaseClusterFixture
        {
            public const string StreamProviderName = GeneratedStreamTestConstants.StreamProviderName;
            public static readonly string StreamProviderTypeName = typeof(GeneratorStreamProvider).FullName;
            public const string StreamNamespace = GeneratedEventCollectorGrain.StreamNamespace;

            public readonly static GeneratorAdapterConfig AdapterConfig = new GeneratorAdapterConfig(StreamProviderName)
            {
                TotalQueueCount = 4,
            };

            protected override TestingSiloHost CreateClusterHost()
            {
                return new TestingSiloHost(new TestingSiloOptions
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
                        config.GetOrCreateNodeConfigurationForSilo("Primary");
                        config.GetOrCreateNodeConfigurationForSilo("Secondary_1");
                    }
                });
            }
        }

        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
        
        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Streaming")]
        public async Task ValidateControllableGeneratedStreamsTest()
        {
            logger.Info("************************ ValidateControllableGeneratedStreamsTest *********************************");
            await ValidateControllableGeneratedStreams();
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Streaming")]
        public async Task Validate2ControllableGeneratedStreamsTest()
        {
            logger.Info("************************ Validate2ControllableGeneratedStreamsTest *********************************");
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

                var mgmt = GrainClient.GrainFactory.GetGrain<IManagementGrain>(0);
                object[] results = await mgmt.SendControlCommandToProvider(Fixture.StreamProviderTypeName, Fixture.StreamProviderName, (int)StreamGeneratorCommand.Configure, generatorConfig);
                Assert.AreEqual(2, results.Length, "expected responses");
                bool[] bResults = results.Cast<bool>().ToArray();
                foreach (var result in bResults)
                {
                    Assert.AreEqual(true, result, "Control command result");
                }

                await TestingUtils.WaitUntilAsync(assertIsTrue => CheckCounters(generatorConfig, assertIsTrue), Timeout);
            }
            finally
            {
                var reporter = GrainClient.GrainFactory.GetGrain<IGeneratedEventReporterGrain>(GeneratedStreamTestConstants.ReporterId);
                reporter.Reset().Ignore();
            }
        }

        private async Task<bool> CheckCounters(SimpleGeneratorConfig generatorConfig, bool assertIsTrue)
        {
            var reporter = GrainClient.GrainFactory.GetGrain<IGeneratedEventReporterGrain>(GeneratedStreamTestConstants.ReporterId);

            var report = await reporter.GetReport(GeneratedStreamTestConstants.StreamProviderName, GeneratedEventCollectorGrain.StreamNamespace);
            if (assertIsTrue)
            {
                // one stream per queue
                Assert.AreEqual(Fixture.AdapterConfig.TotalQueueCount, report.Count, "Stream count");
                foreach (int eventsPerStream in report.Values)
                {
                    Assert.AreEqual(generatorConfig.EventsInStream, eventsPerStream, "Events per stream");
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
