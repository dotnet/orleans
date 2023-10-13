using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.TestingHost;
using Tester.StreamingTests;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace Tester.AzureUtils.Streaming
{
    [TestCategory("BVT"), TestCategory("Streaming"), TestCategory("AQStreaming")]
    public class AQProgrammaticSubscribeTest : ProgrammaticSubcribeTestsRunner, IClassFixture<AQProgrammaticSubscribeTest.Fixture>
    {
        private const int queueCount = 8;
        public class Fixture : BaseAzureTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.AddSiloBuilderConfigurator<TestClusterConfigurator>();
                builder.AddClientBuilderConfigurator<TestClusterConfigurator>();
            }

            private class TestClusterConfigurator : ISiloConfigurator, IClientBuilderConfigurator
            {
                public void Configure(ISiloBuilder hostBuilder)
                {
                    hostBuilder
                        .AddAzureQueueStreams(StreamProviderName2, ob=>ob.Configure<IOptions<ClusterOptions>>(
                            (options, dep) =>
                            {
                                options.ConfigureTestDefaults();
                                options.QueueNames = AzureQueueUtilities.GenerateQueueNames($"{dep.Value.ClusterId}2", queueCount);
                        }))
                        .AddAzureQueueStreams(StreamProviderName, ob => ob.Configure<IOptions<ClusterOptions>>(
                            (options, dep) =>
                            {
                                options.ConfigureTestDefaults();
                                options.QueueNames = AzureQueueUtilities.GenerateQueueNames(dep.Value.ClusterId, queueCount);
                        }));
                    hostBuilder
                        .AddMemoryGrainStorageAsDefault()
                        .AddMemoryGrainStorage("PubSubStore");
                }

                public void Configure(IConfiguration configuration, IClientBuilder clientBuilder) => clientBuilder.AddStreaming();
            }

            public override async Task DisposeAsync()
            {
                await base.DisposeAsync();

                // Only perform cleanup if this suite was not skipped.
                if (!string.IsNullOrWhiteSpace(TestDefaultConfiguration.DataConnectionString))
                {
                    await AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(NullLoggerFactory.Instance,
                        AzureQueueUtilities.GenerateQueueNames(this.HostedCluster.Options.ClusterId, queueCount), new AzureQueueOptions().ConfigureTestDefaults());
                    await AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(NullLoggerFactory.Instance,
                        AzureQueueUtilities.GenerateQueueNames($"{this.HostedCluster.Options.ClusterId}2", queueCount), new AzureQueueOptions().ConfigureTestDefaults());
                }
            }
        }

        public AQProgrammaticSubscribeTest(ITestOutputHelper output, Fixture fixture)
            : base(fixture)
        {
            fixture.EnsurePreconditionsMet();
        }
    }

}
