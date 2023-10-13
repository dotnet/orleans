using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.TestingHost;
using Tester.StreamingTests.Filtering;
using TestExtensions;
using UnitTests.StreamingTests;
using Xunit;

namespace Tester.AzureUtils.Streaming
{
    public class AQStreamFilteringTests : StreamFilteringTestsBase, IClassFixture<AQStreamFilteringTests.Fixture>, IAsyncLifetime
    {
        private const int queueCount = 1;

        public AQStreamFilteringTests(Fixture fixture) : base(fixture)
        {
            fixture.EnsurePreconditionsMet();
        }

        public class Fixture : BaseAzureTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                TestUtils.CheckForAzureStorage();
                builder.AddClientBuilderConfigurator<ClientConfigurator>();
                builder.AddSiloBuilderConfigurator<SiloConfigurator>();
            }

            public class SiloConfigurator : ISiloConfigurator
            {
                public void Configure(ISiloBuilder hostBuilder)
                {
                    hostBuilder
                        .AddAzureQueueStreams(StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME, ob => ob.Configure<IOptions<ClusterOptions>>(
                            (options, dep) =>
                            {
                                options.ConfigureTestDefaults();
                                options.QueueNames = AzureQueueUtilities.GenerateQueueNames(dep.Value.ClusterId, queueCount);
                            }))
                        .AddMemoryGrainStorage("MemoryStore")
                        .AddMemoryGrainStorage("PubSubStore")
                        .AddStreamFilter<CustomStreamFilter>(StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME);
                }
            }

            public class ClientConfigurator : IClientBuilderConfigurator
            {
                public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
                {
                    clientBuilder
                        .AddAzureQueueStreams(StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME, ob => ob.Configure<IOptions<ClusterOptions>>(
                            (options, dep) =>
                            {
                                options.ConfigureTestDefaults();
                                options.QueueNames = AzureQueueUtilities.GenerateQueueNames(dep.Value.ClusterId, queueCount);
                            }))
                        .AddStreamFilter<CustomStreamFilter>(StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME);
                }
            }

            protected override void CheckPreconditionsOrThrow()
            {
                TestUtils.CheckForEventHub();
            }
        }

        protected override string ProviderName => StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME;

        protected override TimeSpan WaitTime => TimeSpan.FromSeconds(2);

        [SkippableFact, TestCategory("BVT"), TestCategory("Streaming"), TestCategory("Filters")]
        public async override Task IgnoreBadFilter() => await base.IgnoreBadFilter();

        [SkippableFact, TestCategory("BVT"), TestCategory("Streaming"), TestCategory("Filters")]
        public async override Task OnlyEvenItems() => await base.OnlyEvenItems();

        [SkippableFact, TestCategory("BVT"), TestCategory("Streaming"), TestCategory("Filters")]
        public async override Task MultipleSubscriptionsDifferentFilterData() => await base.MultipleSubscriptionsDifferentFilterData();

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            if (!string.IsNullOrWhiteSpace(TestDefaultConfiguration.DataConnectionString))
            {
                await AzureQueueStreamProviderUtils.ClearAllUsedAzureQueues(
                  NullLoggerFactory.Instance,
                  AzureQueueUtilities.GenerateQueueNames(this.fixture.HostedCluster.Options.ClusterId, queueCount),
                  new AzureQueueOptions().ConfigureTestDefaults());
            }
        }
    }
}
