using Microsoft.Extensions.Configuration;
using Orleans.Streams;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.StreamingTests;
using Xunit;
using Xunit.Abstractions;

namespace Tester.Redis.Streaming;

[TestCategory("Redis"), TestCategory("Streaming")]
public sealed class RedisStreamBatchingTests : StreamBatchingTestRunner, IClassFixture<RedisStreamBatchingTests.Fixture>
{
    public class Fixture : BaseTestClusterFixture
    {
        private const int PartitionCount = 1;

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
            builder.AddClientBuilderConfigurator<MyClientBuilderConfigurator>();
        }

        protected override void CheckPreconditionsOrThrow() => TestUtils.CheckForRedis();

        private sealed class MyClientBuilderConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder.AddRedisStreams(StreamBatchingTestConst.ProviderName, builder =>
                {
                    builder.ConfigureRedis(optionsBuilder => optionsBuilder.Configure(options =>
                    {
                        options.ConfigurationOptions = RedisStreamTestUtils.GetConfigurationOptions();
                        options.EntryExpiry = TimeSpan.FromHours(1);
                    }));
                    builder.ConfigurePartitioning(PartitionCount);
                    builder.ConfigureStreamPubSub(StreamPubSubType.ImplicitOnly);
                });
            }
        }

        private sealed class MySiloBuilderConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder
                    .AddMemoryGrainStorage("PubSubStore")
                    .AddRedisStreams(StreamBatchingTestConst.ProviderName, builder =>
                    {
                        builder.ConfigureRedis(optionsBuilder => optionsBuilder.Configure(options =>
                        {
                            options.ConfigurationOptions = RedisStreamTestUtils.GetConfigurationOptions();
                            options.EntryExpiry = TimeSpan.FromHours(1);
                        }));
                        builder.ConfigurePartitioning(PartitionCount);
                        builder.ConfigurePullingAgent(optionsBuilder => optionsBuilder.Configure(options => options.BatchContainerBatchSize = 10));
                        builder.ConfigureStreamPubSub(StreamPubSubType.ImplicitOnly);
                    });
            }
        }
    }

    public RedisStreamBatchingTests(Fixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
        fixture.EnsurePreconditionsMet();
    }
}
