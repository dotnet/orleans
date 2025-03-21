using Microsoft.Extensions.Configuration;
using Orleans.Streams;
using Orleans.TestingHost;
using StackExchange.Redis;
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
        private const int partitionCount = 1;

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
            builder.AddClientBuilderConfigurator<MyClientBuilderConfigurator>();
        }

        protected override void CheckPreconditionsOrThrow()
        {
            try
            {
                _ = ConfigurationOptions.Parse(TestDefaultConfiguration.RedisConnectionString);
            }
            catch (Exception exception)
            {
                throw new SkipException("Redis connection string not configured.", exception);
            }

            base.CheckPreconditionsOrThrow();
        }

        private class MyClientBuilderConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder.AddRedisStreams(StreamBatchingTestConst.ProviderName, b =>
                {
                    b.ConfigureRedis(b => b.Configure(options =>
                    {
                        options.ConfigurationOptions = ConfigurationOptions.Parse(TestDefaultConfiguration.RedisConnectionString);
                        options.EntryExpiry = TimeSpan.FromHours(1);
                    }));
                    b.ConfigurePartitioning(partitionCount);
                    b.ConfigureStreamPubSub(StreamPubSubType.ImplicitOnly);
                });
            }
        }

        private class MySiloBuilderConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder
                    .AddMemoryGrainStorage("PubSubStore")
                    .AddRedisStreams(StreamBatchingTestConst.ProviderName, b =>
                    {
                        b.ConfigureRedis(b => b.Configure(options =>
                        {
                            options.ConfigurationOptions = ConfigurationOptions.Parse(TestDefaultConfiguration.RedisConnectionString);
                            options.EntryExpiry = TimeSpan.FromHours(1);
                        }));
                        b.ConfigurePartitioning(partitionCount);
                        b.ConfigurePullingAgent(ob => ob.Configure(options => options.BatchContainerBatchSize = 10));
                        b.ConfigureStreamPubSub(StreamPubSubType.ImplicitOnly);
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
