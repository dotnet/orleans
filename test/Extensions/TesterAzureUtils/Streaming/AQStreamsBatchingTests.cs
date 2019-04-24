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
    [TestCategory("AQStreaming"), TestCategory("Azure")]
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

            private class SiloBuilderConfigurator : ISiloBuilderConfigurator
            {
                public void Configure(ISiloHostBuilder hostBuilder)
                {
                    hostBuilder
                        .AddAzureQueueStreams<AzureQueueDataAdapterV2>(StreamBatchingTestConst.ProviderName, b => b
                            .ConfigureAzureQueue(ob => ob.Configure<IOptions<ClusterOptions>>(
                                (options, dep) =>
                                {
                                    options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
                                    options.QueueNames = AzureQueueUtilities.GenerateQueueNames(dep.Value.ClusterId, queueCount);
                                }))
                            .Configure<StreamPullingAgentOptions>(ob => ob.Configure(options =>
                            {
                                options.BatchContainerBatchSize = 10;
                            }))
                            .ConfigureStreamPubSub(StreamPubSubType.ImplicitOnly));
                }
            }

            private class ClientBuilderConfigurator : IClientBuilderConfigurator
            {
                public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
                {
                    clientBuilder
                        .AddAzureQueueStreams<AzureQueueDataAdapterV2>(StreamBatchingTestConst.ProviderName, b => b
                            .ConfigureAzureQueue(ob => ob.Configure<IOptions<ClusterOptions>>(
                                    (options, dep) =>
                                    {
                                        options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
                                        options.QueueNames = AzureQueueUtilities.GenerateQueueNames(dep.Value.ClusterId, queueCount);
                                    }))
                            .ConfigureStreamPubSub(StreamPubSubType.ImplicitOnly));
                }
            }

            public override void Dispose()
            {
                base.Dispose();
                AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(NullLoggerFactory.Instance,
                    AzureQueueUtilities.GenerateQueueNames(this.HostedCluster.Options.ClusterId, queueCount),
                    TestDefaultConfiguration.DataConnectionString).Wait();
                AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(NullLoggerFactory.Instance,
                    AzureQueueUtilities.GenerateQueueNames($"{this.HostedCluster.Options.ClusterId}2", queueCount),
                    TestDefaultConfiguration.DataConnectionString).Wait();
            }
        }

        public AQStreamsBatchingTests(Fixture fixture, ITestOutputHelper output)
            : base(fixture, output)
        {
            fixture.EnsurePreconditionsMet();
        }
    }
}
