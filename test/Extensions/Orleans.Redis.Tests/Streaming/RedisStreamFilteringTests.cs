using Microsoft.Extensions.Configuration;
using Orleans.TestingHost;
using Tester.StreamingTests.Filtering;
using TestExtensions;
using Xunit;

namespace Tester.Redis.Streaming;

[TestCategory("Redis"), TestCategory("Streaming"), TestCategory("Filters")]
public sealed class RedisStreamFilteringTests : StreamFilteringTestsBase, IClassFixture<RedisStreamFilteringTests.Fixture>
{
    public RedisStreamFilteringTests(Fixture fixture)
        : base(fixture)
    {
        fixture.EnsurePreconditionsMet();
    }

    public sealed class Fixture : BaseTestClusterFixture
    {
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.AddClientBuilderConfigurator<TestClusterConfigurator>();
            builder.AddSiloBuilderConfigurator<TestClusterConfigurator>();
        }

        public override async Task DisposeAsync()
        {
            var serviceId = HostedCluster?.Options.ServiceId;
            await base.DisposeAsync();
            await RedisStreamTestUtils.DeleteServiceKeysAsync(serviceId);
        }

        protected override void CheckPreconditionsOrThrow() => TestUtils.CheckForRedis();

        private sealed class TestClusterConfigurator : ISiloConfigurator, IClientBuilderConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder
                    .AddMemoryGrainStorage("MemoryStore")
                    .AddMemoryGrainStorage("PubSubStore")
                    .AddRedisStreams(RedisStreamTests.StreamProviderName, builder =>
                    {
                        builder.ConfigureRedis(optionsBuilder => optionsBuilder.Configure(options =>
                        {
                            options.ConfigurationOptions = RedisStreamTestUtils.GetConfigurationOptions();
                            options.EntryExpiry = TimeSpan.FromHours(1);
                        }));
                        builder.ConfigurePartitioning(1);
                    })
                    .AddStreamFilter<CustomStreamFilter>(RedisStreamTests.StreamProviderName);
            }

            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder
                    .AddRedisStreams(RedisStreamTests.StreamProviderName, builder =>
                    {
                        builder.ConfigureRedis(optionsBuilder => optionsBuilder.Configure(options =>
                        {
                            options.ConfigurationOptions = RedisStreamTestUtils.GetConfigurationOptions();
                            options.EntryExpiry = TimeSpan.FromHours(1);
                        }));
                        builder.ConfigurePartitioning(1);
                    })
                    .AddStreamFilter<CustomStreamFilter>(RedisStreamTests.StreamProviderName);
            }
        }
    }

    protected override string ProviderName => RedisStreamTests.StreamProviderName;

    protected override TimeSpan WaitTime => TimeSpan.FromSeconds(2);
}
