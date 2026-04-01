using Microsoft.Extensions.Configuration;
using Orleans.Runtime;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using Tester.StreamingTests.Filtering;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace Tester.Redis.Streaming;

[TestCategory("Redis"), TestCategory("Streaming"), TestCategory("Filters")]
public sealed class RedisStreamFilteringTests : StreamFilteringTestsBase, IClassFixture<RedisStreamFilteringTests.Fixture>
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

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

    [SkippableFact, TestCategory("BVT"), TestCategory("Filters")]
    public override async Task IgnoreBadFilter()
    {
        const int numberOfEvents = 10;
        var streamId = StreamId.Create("IgnoreBadFilter", "my-stream");
        var grain = this.fixture.Client.GetGrain<IStreamingHistoryGrain>("IgnoreBadFilter");

        try
        {
            await grain.BecomeConsumer(streamId, ProviderName, "throw");

            var stream = this.fixture.Client.GetStreamProvider(ProviderName).GetStream<int>(streamId);
            for (var i = 0; i < numberOfEvents; i++)
            {
                await stream.OnNextAsync(i);
            }

            await WaitForHistoryCount(grain, numberOfEvents);

            var history = await grain.GetReceivedItems();
            Assert.Equal(numberOfEvents, history.Count);
            for (var i = 0; i < numberOfEvents; i++)
            {
                Assert.Equal(i, history[i]);
            }
        }
        finally
        {
            await grain.StopBeingConsumer();
        }
    }

    [SkippableFact, TestCategory("BVT"), TestCategory("Filters")]
    public override async Task OnlyEvenItems()
    {
        const int numberOfEvents = 10;
        var streamId = StreamId.Create("OnlyEvenItems", "my-stream");
        var grain = this.fixture.Client.GetGrain<IStreamingHistoryGrain>("OnlyEvenItems");

        try
        {
            await grain.BecomeConsumer(streamId, ProviderName, "even");

            var stream = this.fixture.Client.GetStreamProvider(ProviderName).GetStream<int>(streamId);
            for (var i = 0; i < numberOfEvents; i++)
            {
                await stream.OnNextAsync(i);
            }

            await WaitForHistoryCount(grain, numberOfEvents / 2);

            var history = await grain.GetReceivedItems();
            var idx = 0;
            for (var i = 0; i < numberOfEvents; i++)
            {
                if (i % 2 == 0)
                {
                    Assert.Equal(i, history[idx]);
                    idx++;
                }
            }
        }
        finally
        {
            await grain.StopBeingConsumer();
        }
    }

    [SkippableFact, TestCategory("BVT"), TestCategory("Filters")]
    public override async Task MultipleSubscriptionsDifferentFilterData()
    {
        const int numberOfEvents = 10;
        var streamId = StreamId.Create("MultipleSubscriptionsDifferentFilterData", "my-stream");
        var grain = this.fixture.Client.GetGrain<IStreamingHistoryGrain>("MultipleSubscriptionsDifferentFilterData");

        try
        {
            await grain.BecomeConsumer(streamId, ProviderName, "only3");
            await grain.BecomeConsumer(streamId, ProviderName, "only7");

            var stream = this.fixture.Client.GetStreamProvider(ProviderName).GetStream<int>(streamId);
            for (var i = 0; i < numberOfEvents; i++)
            {
                await stream.OnNextAsync(i);
            }

            await WaitForHistoryCount(grain, 2);

            var history = await grain.GetReceivedItems();
            Assert.Equal(2, history.Count);
            Assert.Contains(3, history);
            Assert.Contains(7, history);
        }
        finally
        {
            await grain.StopBeingConsumer();
        }
    }

    private static Task WaitForHistoryCount(IStreamingHistoryGrain grain, int expectedCount) =>
        TestingUtils.WaitUntilAsync(async lastTry =>
        {
            var history = await grain.GetReceivedItems();
            if (lastTry)
            {
                Assert.Equal(expectedCount, history.Count);
            }

            return history.Count == expectedCount;
        }, Timeout, TimeSpan.FromMilliseconds(200));
}
