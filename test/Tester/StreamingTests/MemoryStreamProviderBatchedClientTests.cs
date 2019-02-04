
using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace Tester.StreamingTests
{
    public class MemoryStreamProviderBatchedClientTests : OrleansTestingBase, IClassFixture<MemoryStreamProviderBatchedClientTests.Fixture>
    {
        public class Fixture : BaseTestClusterFixture
        {
            public const string StreamProviderName = "MemoryStreamProvider";
            public const string StreamNamespace = "StreamNamespace";
            private const int partitionCount = 8;
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.ConfigureLegacyConfiguration(legacy =>
                {
                    AdjustConfig(legacy.ClusterConfiguration);
                });
                builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
                builder.AddClientBuilderConfigurator<MyClientBuilderConfigurator>();
            }

            private class MyClientBuilderConfigurator : IClientBuilderConfigurator
            {
                public void Configure(IConfiguration configuration, IClientBuilder clientBuilder) => clientBuilder
                        .AddMemoryStreams<DefaultMemoryMessageBodySerializer>(StreamProviderName, b => b
                    .ConfigurePartitioning(partitionCount)
                    .Configure<StreamPullingAgentOptions>(ob => ob.Configure(options =>
                    {
                        options.BatchContainerBatchSize = 10;
                    })));
            }

            private class MySiloBuilderConfigurator : ISiloBuilderConfigurator
            {
                public void Configure(ISiloHostBuilder hostBuilder) => hostBuilder.AddMemoryGrainStorage("PubSubStore")
                        .AddMemoryStreams<DefaultMemoryMessageBodySerializer>(StreamProviderName, b => b
                    .ConfigurePartitioning(partitionCount));
            }

            private static void AdjustConfig(ClusterConfiguration config)
            {
                // register stream provider
                config.Globals.ClientDropTimeout = TimeSpan.FromSeconds(5);
            }
        }

        private readonly ITestOutputHelper output = null;
        private readonly ClientStreamTestRunner runner;

        private Fixture fixture;

        public MemoryStreamProviderBatchedClientTests(Fixture fixture)
        {
            this.fixture = fixture;
            runner = new ClientStreamTestRunner(fixture.HostedCluster);
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task BatchedMemoryStreamProducerOnDroppedClientTest()
        {
            this.fixture.Logger.Info("************************ BatchedMemoryStreamProducerOnDroppedClientTest *********************************");
            await runner.StreamProducerOnDroppedClientTest(Fixture.StreamProviderName, Fixture.StreamNamespace);
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task BatchedMemoryStreamConsumerOnDroppedClientTest()
        {
            this.fixture.Logger.Info("************************ BatchedMemoryStreamConsumerOnDroppedClientTest *********************************");
            await runner.StreamConsumerOnDroppedClientTest(Fixture.StreamProviderName, Fixture.StreamNamespace, output,
                    null, true);
        }
    }
}
