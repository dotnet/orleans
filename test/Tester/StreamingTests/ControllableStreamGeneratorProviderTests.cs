
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
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

namespace UnitTests.StreamingTests
{
    public class ControllableStreamGeneratorProviderTests : OrleansTestingBase, IClassFixture<ControllableStreamGeneratorProviderTests.Fixture>
    {
        private const int TotalQueueCount = 4;
        private readonly Fixture fixture;

        public class Fixture : BaseTestClusterFixture
        {
            public const string StreamProviderName = GeneratedStreamTestConstants.StreamProviderName;
            public static readonly string StreamProviderTypeName = typeof(PersistentStreamProvider).FullName;
            public const string StreamNamespace = GeneratedEventCollectorGrain.StreamNamespace;

            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
            }

            private class MySiloBuilderConfigurator : ISiloConfigurator
            {
                public void Configure(ISiloBuilder hostBuilder)
                {
                    hostBuilder
                        .AddPersistentStreams(StreamProviderName,
                            GeneratorAdapterFactory.Create,
                            b =>
                            {
                                b.ConfigureStreamPubSub(StreamPubSubType.ImplicitOnly);
                                b.Configure<HashRingStreamQueueMapperOptions>(ob => ob.Configure(options => options.TotalQueueCount = TotalQueueCount));
                                b.UseDynamicClusterConfigDeploymentBalancer();
                            });
                }
            }
        }

        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        public ControllableStreamGeneratorProviderTests(Fixture fixture)
        {
            this.fixture = fixture;
        }
        
        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task ValidateControllableGeneratedStreamsTest()
        {
            this.fixture.Logger.LogInformation("************************ ValidateControllableGeneratedStreamsTest *********************************");
            await ValidateControllableGeneratedStreams();
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task Validate2ControllableGeneratedStreamsTest()
        {
            this.fixture.Logger.LogInformation("************************ Validate2ControllableGeneratedStreamsTest *********************************");
            await ValidateControllableGeneratedStreams();
            await ValidateControllableGeneratedStreams();
        }

        private async Task ValidateControllableGeneratedStreams()
        {
            try
            {
                var generatorConfig = new SimpleGeneratorOptions
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

        private async Task<bool> CheckCounters(SimpleGeneratorOptions generatorConfig, bool assertIsTrue)
        {
            var reporter = this.fixture.GrainFactory.GetGrain<IGeneratedEventReporterGrain>(GeneratedStreamTestConstants.ReporterId);

            var report = await reporter.GetReport(GeneratedStreamTestConstants.StreamProviderName, GeneratedEventCollectorGrain.StreamNamespace);
            if (assertIsTrue)
            {
                // one stream per queue
                Assert.Equal(TotalQueueCount, report.Count); // stream count
                foreach (int eventsPerStream in report.Values)
                {
                    Assert.Equal(generatorConfig.EventsInStream, eventsPerStream);
                }
            }
            else if (TotalQueueCount != report.Count ||
                     report.Values.Any(count => count != generatorConfig.EventsInStream))
            {
                return false;
            }
            return true;
        }
    }
}
