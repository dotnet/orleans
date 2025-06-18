using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Providers;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace Tester.StreamingTests
{
    /// <summary>
    /// Tests for client behavior with the MemoryStreamProvider.
    /// 
    /// MemoryStreamProvider is an in-memory streaming provider useful for:
    /// - Unit testing streaming scenarios without external dependencies
    /// - Development and prototyping
    /// - Scenarios where persistence is not required
    /// 
    /// These tests focus on client disconnection scenarios to ensure
    /// proper cleanup and recovery of stream producers and consumers.
    /// </summary>
    public class MemoryStreamProviderClientTests : OrleansTestingBase, IClassFixture<MemoryStreamProviderClientTests.Fixture>
    {
        /// <summary>
        /// Test fixture that configures a cluster with MemoryStreamProvider.
        /// Sets up both silo and client with the same stream provider configuration
        /// to ensure proper communication.
        /// </summary>
        public class Fixture : BaseTestClusterFixture
        {
            public const string StreamProviderName = "MemoryStreamProvider";
            public const string StreamNamespace = "StreamNamespace";
            private const int partitionCount = 8;
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
                builder.AddClientBuilderConfigurator<MyClientBuilderConfigurator>();
            }

            private class MyClientBuilderConfigurator : IClientBuilderConfigurator
            {
                public void Configure(IConfiguration configuration, IClientBuilder clientBuilder) => clientBuilder
                        .AddMemoryStreams<DefaultMemoryMessageBodySerializer>(StreamProviderName, b=>b
                    .ConfigurePartitioning(partitionCount));
            }

            /// <summary>
            /// Configures silos with:
            /// - Memory grain storage for PubSub subscriptions
            /// - Memory stream provider with specified partition count
            /// - Reduced client drop timeout for faster test execution
            /// </summary>
            private class MySiloBuilderConfigurator : ISiloConfigurator
            {
                public void Configure(ISiloBuilder hostBuilder)=> hostBuilder.AddMemoryGrainStorage("PubSubStore")
                        .AddMemoryStreams<DefaultMemoryMessageBodySerializer>(StreamProviderName, b=>b
                    .ConfigurePartitioning(partitionCount))
                    .Configure<SiloMessagingOptions>(options => options.ClientDropTimeout = TimeSpan.FromSeconds(5));
            }
        }

        private readonly ITestOutputHelper output = null;
        private readonly ClientStreamTestRunner runner;

        private readonly Fixture fixture;

        public MemoryStreamProviderClientTests(Fixture fixture)
        {
            this.fixture = fixture;
            runner = new ClientStreamTestRunner(fixture.HostedCluster);
        }

        /// <summary>
        /// Tests stream producer behavior when the client is dropped/disconnected.
        /// Verifies that:
        /// - Stream production stops when client disconnects
        /// - No orphaned producers remain in the system
        /// - Other clients/grains can still use the stream
        /// </summary>
        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task MemoryStreamProducerOnDroppedClientTest()
        {
            this.fixture.Logger.LogInformation("************************ MemoryStreamProducerOnDroppedClientTest *********************************");
            await runner.StreamProducerOnDroppedClientTest(Fixture.StreamProviderName, Fixture.StreamNamespace);
        }

        /// <summary>
        /// Tests stream consumer behavior when the client is dropped/disconnected.
        /// Verifies that:
        /// - Consumer subscriptions are properly cleaned up
        /// - Messages are not lost when a consumer disconnects
        /// - Rebalancing occurs to redistribute stream processing
        /// </summary>
        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task MemoryStreamConsumerOnDroppedClientTest()
        {
            this.fixture.Logger.LogInformation("************************ MemoryStreamConsumerOnDroppedClientTest *********************************");
            await runner.StreamConsumerOnDroppedClientTest(Fixture.StreamProviderName, Fixture.StreamNamespace, output,
                    null, true);
        }
    }
}
