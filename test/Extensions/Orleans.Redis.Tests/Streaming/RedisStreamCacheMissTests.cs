using Microsoft.Extensions.Configuration;
using Orleans.Configuration;
using Orleans.Streams;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using Tester.StreamingTests;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;
using Xunit.Abstractions;

namespace Tester.Redis.Streaming;

[TestCategory("Redis"), TestCategory("Streaming"), TestCategory("Functional"), TestCategory("StreamingCacheMiss")]
public sealed class RedisStreamCacheMissTests : StreamingCacheMissTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

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
                    builder.ConfigureRedis(optionsBuilder => optionsBuilder.Configure(options =>
                    {
                        options.ConfigurationOptions = RedisStreamTestUtils.GetConfigurationOptions();
                        options.EntryExpiry = TimeSpan.FromHours(1);
                        options.CheckpointPersistInterval = TimeSpan.Zero;
                    }));
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
                builder.ConfigureRedis(optionsBuilder => optionsBuilder.Configure(options =>
                {
                    options.ConfigurationOptions = RedisStreamTestUtils.GetConfigurationOptions();
                    options.EntryExpiry = TimeSpan.FromHours(1);
                }));
                builder.ConfigurePartitioning(1);
            });
        }
    }

    public override async Task PreviousEventEvictedFromCacheTest()
    {
        var streamProvider = this.Client.GetStreamProvider(StreamProviderName);

        var key = Guid.NewGuid();
        var stream = streamProvider.GetStream<byte[]>(nameof(IImplicitSubscriptionCounterGrain), key);
        var grain = this.Client.GetGrain<IImplicitSubscriptionCounterGrain>(key);

        var otherStreams = new List<IAsyncStream<byte[]>>();
        for (var i = 0; i < 20; i++)
        {
            otherStreams.Add(streamProvider.GetStream<byte[]>(nameof(IImplicitSubscriptionCounterGrain), Guid.NewGuid()));
        }

        var interestingData = new byte[1024];
        interestingData[0] = 1;

        await stream.OnNextAsync(interestingData);
        await WaitForEventCount(grain, 1);

        await stream.OnNextAsync(interestingData);

        await Task.Delay(TimeSpan.FromSeconds(6));
        await Task.WhenAll(otherStreams.Select(s => s.OnNextAsync(interestingData)));

        await stream.OnNextAsync(interestingData);
        await WaitForEventCount(grain, 3);

        Assert.Equal(0, await grain.GetErrorCounter());
        Assert.Equal(3, await grain.GetEventCounter());
    }

    public override async Task PreviousEventEvictedFromCacheWithFilterTest()
    {
        var streamProvider = this.Client.GetStreamProvider(StreamProviderName);

        var key = Guid.NewGuid();
        var stream = streamProvider.GetStream<byte[]>(nameof(IImplicitSubscriptionCounterGrain), key);
        var grain = this.Client.GetGrain<IImplicitSubscriptionCounterGrain>(key);

        var otherStreams = new List<IAsyncStream<byte[]>>();
        for (var i = 0; i < 20; i++)
        {
            otherStreams.Add(streamProvider.GetStream<byte[]>(nameof(IImplicitSubscriptionCounterGrain), Guid.NewGuid()));
        }

        var skippedData = new byte[1024];
        skippedData[0] = 2;

        var interestingData = new byte[1024];
        interestingData[0] = 1;

        await stream.OnNextAsync(interestingData);
        await WaitForEventCount(grain, 1);

        await stream.OnNextAsync(skippedData);

        await Task.Delay(TimeSpan.FromSeconds(6));
        await Task.WhenAll(otherStreams.Select(s => s.OnNextAsync(skippedData)));

        await stream.OnNextAsync(interestingData);
        await WaitForEventCount(grain, 2);

        Assert.Equal(0, await grain.GetErrorCounter());
        Assert.Equal(2, await grain.GetEventCounter());
    }

    private static Task WaitForEventCount(IImplicitSubscriptionCounterGrain grain, int expectedCount) =>
        TestingUtils.WaitUntilAsync(async lastTry =>
        {
            var actual = await grain.GetEventCounter();
            if (lastTry)
            {
                Assert.Equal(expectedCount, actual);
            }

            return actual == expectedCount;
        }, Timeout, TimeSpan.FromMilliseconds(200));
}
