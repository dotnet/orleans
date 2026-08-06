using Microsoft.Extensions.Configuration;
using Orleans.Configuration;
using Orleans.Streams;
using Orleans.TestingHost;
using Tester.StreamingTests;
using TestExtensions;
using Xunit.Abstractions;

namespace Tester.Redis.Streaming;

[TestCategory("Redis"), TestCategory("Streaming"), TestCategory("Functional"), TestCategory("StreamingCacheMiss")]
public sealed class RedisStreamCacheMissTests : StreamingCacheMissTests
{
    public RedisStreamCacheMissTests(ITestOutputHelper output)
        : base(output)
    {
    }

    public override async Task DisposeAsync()
    {
        var serviceId = HostedCluster?.Options.ServiceId;
        await base.DisposeAsync();
        await RedisStreamTestUtils.DeleteServiceKeysAsync(serviceId);
    }

    protected override void CheckPreconditionsOrThrow() => TestUtils.CheckForRedis();

    protected override void ConfigureTestCluster(TestClusterBuilder builder)
    {
        TestUtils.CheckForRedis();
        builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
        builder.AddClientBuilderConfigurator<MyClientBuilderConfigurator>();
    }

    private sealed class MySiloBuilderConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder hostBuilder)
        {
            hostBuilder
                .AddMemoryGrainStorageAsDefault()
                .AddMemoryGrainStorage("PubSubStore")
                .AddRedisStreams(StreamProviderName, builder =>
                {
                    builder.Configure<StreamCacheEvictionOptions>(optionsBuilder => optionsBuilder.Configure(options =>
                    {
                        options.DataMaxAgeInCache = DataMaxAgeInCache;
                        options.DataMinTimeInCache = DataMinTimeInCache;
                        options.MetadataMinTimeInCache = TimeSpan.FromMinutes(1);
                    }));
                    builder.RedisStreamingOptions.Configure(options =>
                    {
                        options.ConfigurationOptions = RedisStreamTestUtils.GetConfigurationOptions();
                        options.EntryExpiry = TimeSpan.FromHours(1);
                        options.CheckpointPersistInterval = TimeSpan.Zero;
                    });
                    builder.ConfigurePartitioning(1);
                    builder.ConfigureStreamPubSub(StreamPubSubType.ImplicitOnly);
                })
                .AddStreamFilter<CustomStreamFilter>(StreamProviderName);
        }
    }

    private sealed class MyClientBuilderConfigurator : IClientBuilderConfigurator
    {
        public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
        {
            clientBuilder.AddRedisStreams(StreamProviderName, builder =>
            {
                builder.RedisStreamingOptions.Configure(options =>
                {
                    options.ConfigurationOptions = RedisStreamTestUtils.GetConfigurationOptions();
                    options.EntryExpiry = TimeSpan.FromHours(1);
                });
                builder.ConfigurePartitioning(1);
            });
        }
    }
}
