using Microsoft.Extensions.Configuration;
using Orleans.Streams;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using Tester.StreamingTests.ProgrammaticSubscribeTests;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using UnitTests.Grains.ProgrammaticSubscribe;
using Xunit;

namespace Tester.Redis.Streaming;

[TestCategory("Redis"), TestCategory("Streaming"), TestCategory("Functional")]
public sealed class RedisSubscriptionObserverWithImplicitSubscribingTests : SubscriptionObserverWithImplicitSubscribingTestRunner, IClassFixture<RedisSubscriptionObserverWithImplicitSubscribingTests.Fixture>
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public RedisSubscriptionObserverWithImplicitSubscribingTests(Fixture fixture)
        : base(fixture)
    {
        fixture.EnsurePreconditionsMet();
    }

    [SkippableFact]
    public override async Task StreamingTests_Consumer_Producer_Subscribe()
    {
        var streamId = new FullStreamIdentity(Guid.NewGuid(), ImplicitSubscribeGrain.StreamNameSpace, StreamProviderName);
        var producer = this.fixture.HostedCluster.GrainFactory.GetGrain<ITypedProducerGrainProducingApple>(Guid.NewGuid());
        await producer.BecomeProducer(streamId.Guid, streamId.Namespace, streamId.ProviderName);

        var implicitConsumer = this.fixture.HostedCluster.GrainFactory.GetGrain<IImplicitSubscribeGrain>(streamId.Guid);

        await WarmUpAsync(producer, implicitConsumer, expectedSubscriptionCallbacks: 1, expectedConsumed: 1);
        await producer.ClearNumberProduced();
        var baselineConsumed = await implicitConsumer.GetNumberConsumed();

        for (var i = 0; i < 10; i++)
        {
            await producer.Produce();
        }

        await WaitForConsumedCountAsync(implicitConsumer, baselineConsumed, producer);
        await implicitConsumer.StopConsuming();
    }

    [SkippableFact]
    public override async Task StreamingTests_Consumer_Producer_SubscribeToTwoStream_MessageWithPolymorphism()
    {
        var streamId = new FullStreamIdentity(Guid.NewGuid(), ImplicitSubscribeGrain.StreamNameSpace, StreamProviderName);
        var producer = this.fixture.GrainFactory.GetGrain<ITypedProducerGrainProducingApple>(Guid.NewGuid());
        await producer.BecomeProducer(streamId.Guid, streamId.Namespace, streamId.ProviderName);

        var streamId2 = new FullStreamIdentity(streamId.Guid, ImplicitSubscribeGrain.StreamNameSpace2, StreamProviderName);
        var producer2 = this.fixture.GrainFactory.GetGrain<ITypedProducerGrainProducingApple>(Guid.NewGuid());
        await producer2.BecomeProducer(streamId2.Guid, streamId2.Namespace, streamId2.ProviderName);

        var implicitConsumer = this.fixture.HostedCluster.GrainFactory.GetGrain<IImplicitSubscribeGrain>(streamId.Guid);

        await WarmUpAsync(producer, implicitConsumer, expectedSubscriptionCallbacks: 1, expectedConsumed: 1);
        await WarmUpAsync(producer2, implicitConsumer, expectedSubscriptionCallbacks: 2, expectedConsumed: 2);
        await producer.ClearNumberProduced();
        await producer2.ClearNumberProduced();
        var baselineConsumed = await implicitConsumer.GetNumberConsumed();

        for (var i = 0; i < 10; i++)
        {
            await producer.Produce();
            if (i < 8)
            {
                await producer2.Produce();
            }
        }

        await WaitForConsumedCountAsync(implicitConsumer, baselineConsumed, producer, producer2);
        await implicitConsumer.StopConsuming();
    }

    [SkippableFact]
    public override async Task StreamingTests_Consumer_Producer_SubscribeToStreamsHandledByDifferentStreamProvider()
    {
        var streamId = new FullStreamIdentity(Guid.NewGuid(), ImplicitSubscribeGrain.StreamNameSpace, StreamProviderName);
        var producer = this.fixture.GrainFactory.GetGrain<ITypedProducerGrainProducingApple>(Guid.NewGuid());
        await producer.BecomeProducer(streamId.Guid, streamId.Namespace, streamId.ProviderName);

        var streamId2 = new FullStreamIdentity(streamId.Guid, ImplicitSubscribeGrain.StreamNameSpace2, StreamProviderName2);
        var producer2 = this.fixture.GrainFactory.GetGrain<ITypedProducerGrainProducingApple>(Guid.NewGuid());
        await producer2.BecomeProducer(streamId2.Guid, streamId2.Namespace, streamId2.ProviderName);

        var implicitConsumer = this.fixture.HostedCluster.GrainFactory.GetGrain<IImplicitSubscribeGrain>(streamId.Guid);

        await WarmUpAsync(producer, implicitConsumer, expectedSubscriptionCallbacks: 1, expectedConsumed: 1);
        await WarmUpAsync(producer2, implicitConsumer, expectedSubscriptionCallbacks: 2, expectedConsumed: 2);
        await producer.ClearNumberProduced();
        await producer2.ClearNumberProduced();
        var baselineConsumed = await implicitConsumer.GetNumberConsumed();

        for (var i = 0; i < 10; i++)
        {
            await producer.Produce();
            if (i < 8)
            {
                await producer2.Produce();
            }
        }

        await WaitForConsumedCountAsync(implicitConsumer, baselineConsumed, producer, producer2);
        await implicitConsumer.StopConsuming();
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
                    builder.ConfigureRedis(optionsBuilder => optionsBuilder.Configure(options =>
                    {
                        options.ConfigurationOptions = RedisStreamTestUtils.GetConfigurationOptions();
                        options.EntryExpiry = TimeSpan.FromHours(1);
                    }));
                    builder.ConfigurePartitioning(1);
                    builder.ConfigureStreamPubSub(StreamPubSubType.ImplicitOnly);
                })
                .AddRedisStreams(StreamProviderName2, builder =>
                {
                    builder.ConfigureRedis(optionsBuilder => optionsBuilder.Configure(options =>
                    {
                        options.ConfigurationOptions = RedisStreamTestUtils.GetConfigurationOptions();
                        options.EntryExpiry = TimeSpan.FromHours(1);
                    }));
                    builder.ConfigurePartitioning(1);
                    builder.ConfigureStreamPubSub(StreamPubSubType.ImplicitOnly);
                })
                .AddMemoryGrainStorageAsDefault()
                .AddMemoryGrainStorage("PubSubStore");
        }

        public void Configure(IConfiguration configuration, IClientBuilder clientBuilder) => clientBuilder.AddStreaming();
    }

    private static Task WarmUpAsync(
        ITypedProducerGrain producer,
        IImplicitSubscribeGrain consumer,
        int expectedSubscriptionCallbacks,
        int expectedConsumed)
        => ProduceAndWaitAsync(producer, consumer, expectedSubscriptionCallbacks, expectedConsumed);

    private static async Task ProduceAndWaitAsync(
        ITypedProducerGrain producer,
        IImplicitSubscribeGrain consumer,
        int expectedSubscriptionCallbacks,
        int expectedConsumed)
    {
        await producer.Produce();
        await TestingUtils.WaitUntilAsync(async lastTry =>
        {
            var subscriptionCallbacks = await consumer.GetCountOfOnAddFuncCalled();
            var consumed = await consumer.GetNumberConsumed();
            if (lastTry)
            {
                Assert.True(subscriptionCallbacks >= expectedSubscriptionCallbacks, $"Expected at least {expectedSubscriptionCallbacks} subscription callbacks, got {subscriptionCallbacks}.");
                Assert.True(consumed >= expectedConsumed, $"Expected at least {expectedConsumed} consumed items, got {consumed}.");
            }

            return subscriptionCallbacks >= expectedSubscriptionCallbacks && consumed >= expectedConsumed;
        }, Timeout, TimeSpan.FromMilliseconds(200));
    }

    private static Task WaitForConsumedCountAsync(IImplicitSubscribeGrain consumer, int baselineConsumed, params ITypedProducerGrain[] producers)
        => TestingUtils.WaitUntilAsync(async lastTry =>
        {
            var produced = 0;
            foreach (var producer in producers)
            {
                produced += await producer.GetNumberProduced();
            }

            var consumed = await consumer.GetNumberConsumed();
            if (lastTry)
            {
                Assert.Equal(baselineConsumed + produced, consumed);
            }

            return baselineConsumed + produced == consumed;
        }, Timeout, TimeSpan.FromMilliseconds(200));
}
