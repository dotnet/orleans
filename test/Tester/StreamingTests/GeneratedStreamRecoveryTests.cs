using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Providers.Streams.Generator;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.TestingHost;
using Tester.StreamingTests;
using TestExtensions;
using TestGrains;
using UnitTests.Grains;
using Xunit;

namespace UnitTests.StreamingTests
{
    public class GeneratedImplicitSubscriptionStreamRecoveryTests : OrleansTestingBase, IClassFixture<GeneratedImplicitSubscriptionStreamRecoveryTests.Fixture>
    {
        private static readonly string StreamProviderTypeName = typeof(PersistentStreamProvider).FullName;
        private const int TotalQueueCount = 4;
        private readonly Fixture fixture;
        private readonly ImplicitSubscritionRecoverableStreamTestRunner runner;

        public class Fixture : BaseTestClusterFixture
        {
            public const string StreamProviderName = GeneratedStreamTestConstants.StreamProviderName;

            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
            }

            private class MySiloBuilderConfigurator : ISiloConfigurator
            {
                public void Configure(ISiloBuilder hostBuilder)
                {
                    hostBuilder
                         .AddMemoryGrainStorageAsDefault()
                        .AddMemoryGrainStorage("MemoryStore")
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

        public GeneratedImplicitSubscriptionStreamRecoveryTests(Fixture fixture)
        {
            this.fixture = fixture;
            this.runner = new ImplicitSubscritionRecoverableStreamTestRunner(
                this.fixture.GrainFactory,
                Fixture.StreamProviderName);
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task Recoverable100EventStreamsWithTransientErrorsTest()
        {
            this.fixture.Logger.LogInformation("************************ Recoverable100EventStreamsWithTransientErrorsTest *********************************");
            await runner.Recoverable100EventStreamsWithTransientErrors(GenerateEvents,
                ImplicitSubscription_TransientError_RecoverableStream_CollectorGrain.StreamNamespace,
                TotalQueueCount,
                100);
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task Recoverable100EventStreamsWith1NonTransientErrorTest()
        {
            this.fixture.Logger.LogInformation("************************ Recoverable100EventStreamsWith1NonTransientErrorTest *********************************");
            await runner.Recoverable100EventStreamsWith1NonTransientError(GenerateEvents,
                ImplicitSubscription_NonTransientError_RecoverableStream_CollectorGrain.StreamNamespace,
                TotalQueueCount,
                100);
        }

        private async Task GenerateEvents(string streamNamespace, int streamCount, int eventsInStream)
        {
            var generatorConfig = new SimpleGeneratorOptions
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
