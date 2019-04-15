using Microsoft.Extensions.Configuration;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers;
using Orleans.Streams;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.StreamingTests
{
    [TestCategory("BVT")]
    public class MemoryStreamBatchingTests : StreamBatchingTestRunner, IClassFixture<MemoryStreamBatchingTests.Fixture>
    {
        public class Fixture : BaseTestClusterFixture
        {
            private const int partitionCount = 1;

            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
                builder.AddClientBuilderConfigurator<MyClientBuilderConfigurator>();
            }

            private class MyClientBuilderConfigurator : IClientBuilderConfigurator
            {
                public void Configure(IConfiguration configuration, IClientBuilder clientBuilder) => clientBuilder
                    .AddMemoryStreams<DefaultMemoryMessageBodySerializer>(StreamBatchingTestConst.ProviderName, b => b
                        .ConfigurePartitioning(partitionCount)
                        .ConfigureStreamPubSub(StreamPubSubType.ImplicitOnly));
            }

            private class MySiloBuilderConfigurator : ISiloBuilderConfigurator
            {
                public void Configure(ISiloHostBuilder hostBuilder) => hostBuilder.AddMemoryGrainStorage("PubSubStore")
                    .AddMemoryStreams<DefaultMemoryMessageBodySerializer>(StreamBatchingTestConst.ProviderName, b => b
                        .ConfigurePartitioning(partitionCount)
                        .ConfigurePullingAgent(ob => ob.Configure(options =>
                        {
                            options.BatchContainerBatchSize = 10;
                        }))
                        .ConfigureStreamPubSub(StreamPubSubType.ImplicitOnly));
            }
        }

        public MemoryStreamBatchingTests(Fixture fixture, ITestOutputHelper output)
            : base(fixture, output)
        {
            fixture.EnsurePreconditionsMet();
        }
    }
}
