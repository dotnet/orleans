using Microsoft.Extensions.Configuration;
using Orleans.TestingHost;
using StackExchange.Redis;
using Tester.StreamingTests;
using TestExtensions;
using Xunit;

namespace Tester.Redis.Streaming;

[TestCategory("Redis"), TestCategory("Streaming"), TestCategory("Functional")]
public sealed class RedisProgrammaticSubscribeTests : ProgrammaticSubscribeTestsRunner, IClassFixture<RedisProgrammaticSubscribeTests.Fixture>
{
    public class Fixture : BaseTestClusterFixture
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
                // Do use "PubSubStore" in this test

                hostBuilder.AddRedisStreams(StreamProviderName, options =>
                {
                    options.ConfigurationOptions = ConfigurationOptions.Parse(TestDefaultConfiguration.RedisConnectionString);
                    options.EntryExpiry = TimeSpan.FromHours(1);
                });
                hostBuilder.AddMemoryGrainStorage(StreamProviderName);

                hostBuilder.AddRedisStreams(StreamProviderName2, options =>
                {
                    options.ConfigurationOptions = ConfigurationOptions.Parse(TestDefaultConfiguration.RedisConnectionString);
                    options.EntryExpiry = TimeSpan.FromHours(1);
                });
                hostBuilder.AddMemoryGrainStorage(StreamProviderName2);
            }

            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder) => clientBuilder.AddStreaming();
        }
    }

    public RedisProgrammaticSubscribeTests(Fixture fixture) : base(fixture)
    {
        fixture.EnsurePreconditionsMet();
    }
}
