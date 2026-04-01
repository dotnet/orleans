using Microsoft.Extensions.Configuration;
using Orleans.TestingHost;
using Tester.StreamingTests;
using TestExtensions;
using Xunit;

namespace Tester.Redis.Streaming;

[TestCategory("Redis"), TestCategory("Streaming")]
public sealed class RedisProgrammaticSubscribeTests : ProgrammaticSubscribeTestsRunner, IClassFixture<RedisProgrammaticSubscribeTests.Fixture>
{
    public class Fixture : BaseTestClusterFixture
    {
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.AddSiloBuilderConfigurator<TestClusterConfigurator>();
            builder.AddClientBuilderConfigurator<TestClusterConfigurator>();
        }

        private sealed class TestClusterConfigurator : ISiloConfigurator, IClientBuilderConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.AddRedisStreams(StreamProviderName, options =>
                {
                    options.ConfigurationOptions = RedisStreamTestUtils.GetConfigurationOptions();
                    options.EntryExpiry = TimeSpan.FromHours(1);
                });
                hostBuilder.AddMemoryGrainStorage(StreamProviderName);

                hostBuilder.AddRedisStreams(StreamProviderName2, options =>
                {
                    options.ConfigurationOptions = RedisStreamTestUtils.GetConfigurationOptions();
                    options.EntryExpiry = TimeSpan.FromHours(1);
                });
                hostBuilder.AddMemoryGrainStorage(StreamProviderName2);
            }

            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder) => clientBuilder.AddStreaming();
        }

        protected override void CheckPreconditionsOrThrow() => TestUtils.CheckForRedis();
    }

    public RedisProgrammaticSubscribeTests(Fixture fixture)
        : base(fixture)
    {
        fixture.EnsurePreconditionsMet();
    }
}
