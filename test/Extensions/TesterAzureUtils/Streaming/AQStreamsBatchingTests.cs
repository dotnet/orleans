using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Streams;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.StreamingTests;
using Xunit;
using Xunit.Abstractions;

namespace Tester.AzureUtils.Streaming
{
    [TestCategory("AQStreaming"), TestCategory("AzureStorage")]
    public class AQStreamsBatchingTests : StreamBatchingTestRunner, IClassFixture<AQStreamsBatchingTests.Fixture>
    {
        private const int queueCount = 8;
        public class Fixture : BaseAzureTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
                builder.AddClientBuilderConfigurator<ClientBuilderConfigurator>();
            }

            private class SiloBuilderConfigurator : ISiloConfigurator
            {
                public void Configure(ISiloBuilder hostBuilder)
                {
                    hostBuilder
                        .AddAzureQueueStreams(StreamBatchingTestConst.ProviderName, b =>
                        {
                            b.ConfigureAzureQueue(ob => ob.Configure<IOptions<ClusterOptions>>(
                                (options, dep) =>
                                {
                                    options.ConfigureTestDefaults();
                                    options.QueueNames = AzureQueueUtilities.GenerateQueueNames(dep.Value.ClusterId, queueCount);
                                }));
                            b.ConfigurePullingAgent(ob => ob.Configure(options =>
                            {
                                options.BatchContainerBatchSize = 10;
                            }));
                            b.ConfigureStreamPubSub(StreamPubSubType.ImplicitOnly);
                        });
                }
            }

            private class ClientBuilderConfigurator : IClientBuilderConfigurator
            {
                public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
                {
                    clientBuilder
                        .AddAzureQueueStreams(StreamBatchingTestConst.ProviderName, b =>
                        {
                            b.ConfigureAzureQueue(ob => ob.Configure<IOptions<ClusterOptions>>(
                                (options, dep) =>
                                {
                                    options.ConfigureTestDefaults();
                                    options.QueueNames = AzureQueueUtilities.GenerateQueueNames(dep.Value.ClusterId, queueCount);
                                }));
                            b.ConfigureStreamPubSub(StreamPubSubType.ImplicitOnly);
                        });
                }
            }

            public override async Task DisposeAsync()
            {
                await base.DisposeAsync();
                if (!string.IsNullOrWhiteSpace(TestDefaultConfiguration.DataConnectionString))
                {
                    await AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(NullLoggerFactory.Instance,
                        AzureQueueUtilities.GenerateQueueNames(this.HostedCluster.Options.ClusterId, queueCount),
                        new AzureQueueOptions().ConfigureTestDefaults());
                    await AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(NullLoggerFactory.Instance,
                        AzureQueueUtilities.GenerateQueueNames($"{this.HostedCluster.Options.ClusterId}2", queueCount),
                        new AzureQueueOptions().ConfigureTestDefaults());
                }
            }
        }

        public AQStreamsBatchingTests(Fixture fixture, ITestOutputHelper output)
            : base(fixture, output)
        {
            fixture.EnsurePreconditionsMet();
        }
    }
}
