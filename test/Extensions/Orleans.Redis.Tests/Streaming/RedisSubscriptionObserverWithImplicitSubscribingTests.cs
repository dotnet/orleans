using Microsoft.Extensions.Configuration;
using Orleans.Streams;
using Orleans.TestingHost;
using Tester.StreamingTests.ProgrammaticSubscribeTests;
using TestExtensions;
using Xunit;

namespace Tester.Redis.Streaming;

[TestCategory("Redis"), TestCategory("Streaming"), TestCategory("Functional")]
public sealed class RedisSubscriptionObserverWithImplicitSubscribingTests : SubscriptionObserverWithImplicitSubscribingTestRunner, IClassFixture<RedisSubscriptionObserverWithImplicitSubscribingTests.Fixture>
{
    public RedisSubscriptionObserverWithImplicitSubscribingTests(Fixture fixture)
        : base(fixture)
    {
        fixture.EnsurePreconditionsMet();
    }

    public sealed class Fixture : BaseTestClusterFixture
    {
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.AddSiloBuilderConfigurator<TestClusterConfigurator>();
            builder.AddClientBuilderConfigurator<TestClusterConfigurator>();
        }

        public override async Task DisposeAsync()
        {
            var serviceId = HostedCluster?.Options.ServiceId;
            await base.DisposeAsync();
            await RedisStreamTestUtils.DeleteServiceKeysAsync(serviceId);
        }

        protected override void CheckPreconditionsOrThrow() => TestUtils.CheckForRedis();
    }

    private sealed class TestClusterConfigurator : ISiloConfigurator, IClientBuilderConfigurator
    {
        public void Configure(ISiloBuilder hostBuilder)
        {
            hostBuilder
                .AddRedisStreams(StreamProviderName, builder =>
                {
                    builder.RedisStreamingOptions.Configure(options =>
                    {
                        options.ConfigurationOptions = RedisStreamTestUtils.GetConfigurationOptions();
                        options.EntryExpiry = TimeSpan.FromHours(1);
                    });
                    builder.ConfigurePartitioning(1);
                    builder.ConfigureStreamPubSub(StreamPubSubType.ImplicitOnly);
                })
                .AddRedisStreams(StreamProviderName2, builder =>
                {
                    builder.RedisStreamingOptions.Configure(options =>
                    {
                        options.ConfigurationOptions = RedisStreamTestUtils.GetConfigurationOptions();
                        options.EntryExpiry = TimeSpan.FromHours(1);
                    });
                    builder.ConfigurePartitioning(1);
                    builder.ConfigureStreamPubSub(StreamPubSubType.ImplicitOnly);
                })
                .AddMemoryGrainStorageAsDefault()
                .AddMemoryGrainStorage("PubSubStore");
        }

        public void Configure(IConfiguration configuration, IClientBuilder clientBuilder) => clientBuilder.AddStreaming();
    }
}
