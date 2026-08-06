using Microsoft.Extensions.Configuration;
using Orleans.Configuration;
using Orleans.Streams;
using Orleans.TestingHost;
using Tester.StreamingTests;
using TestExtensions;

namespace Tester.Redis.Streaming;

[TestCategory("Redis"), TestCategory("Streaming"), TestCategory("StreamingResume")]
public sealed class RedisStreamingResumeTests : StreamingResumeTests
{
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
                .AddMemoryGrainStorage("PubSubStore")
                .AddMemoryGrainStorageAsDefault()
                .AddRedisStreams(StreamProviderName, builder =>
                {
                    builder.ConfigurePullingAgent(optionsBuilder => optionsBuilder.Configure(options => options.StreamInactivityPeriod = StreamInactivityPeriod));
                    builder.Configure<StreamCacheEvictionOptions>(optionsBuilder => optionsBuilder.Configure(options =>
                    {
                        options.MetadataMinTimeInCache = MetadataMinTimeInCache;
                        options.DataMaxAgeInCache = DataMaxAgeInCache;
                        options.DataMinTimeInCache = DataMinTimeInCache;
                    }));
                    builder.RedisStreamingOptions.Configure(options =>
                    {
                        options.ConfigurationOptions = RedisStreamTestUtils.GetConfigurationOptions();
                        options.EntryExpiry = TimeSpan.FromHours(1);
                        options.CheckpointPersistInterval = TimeSpan.Zero;
                    });
                    builder.ConfigurePartitioning(1);
                    builder.ConfigureStreamPubSub(StreamPubSubType.ImplicitOnly);
                });
        }
    }

    private sealed class MyClientBuilderConfigurator : IClientBuilderConfigurator
    {
        public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
        {
            clientBuilder.AddRedisStreams(StreamProviderName, builder =>
            {
                builder.RedisStreamingOptions.Configure(options => options.ConfigurationOptions = RedisStreamTestUtils.GetConfigurationOptions());
                builder.ConfigurePartitioning(1);
            });
        }
    }
}
