#nullable enable
using Orleans.Runtime;
using Orleans.TestingHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Streams.Core;
using TestExtensions;
using Xunit;
using UnitTests.GrainInterfaces;
using Orleans.TestingHost.Utils;
using UnitTests.Grains.ProgrammaticSubscribe;

namespace Tester.StreamingTests;

public abstract class ProgrammaticSubscribeTestsRunner
{
    private const int EventCountPerPhase = 10;
    private readonly BaseTestClusterFixture fixture;
    public const string StreamProviderName = "StreamProvider1";
    public const string StreamProviderName2 = "StreamProvider2";
    public ProgrammaticSubscribeTestsRunner(BaseTestClusterFixture fixture)
    {
        this.fixture = fixture;
    }

    [SkippableFact]
    public async Task Programmatic_Subscribe_Provider_WithExplicitPubsub_TryGetStreamSubscrptionManager()
    {
        var subGrain = this.fixture.HostedCluster.GrainFactory.GetGrain<ISubscribeGrain>(Guid.NewGuid());
        Assert.True(await subGrain.CanGetSubscriptionManager(StreamProviderName));
    }
    
    [SkippableFact]
    public async Task Programmatic_Subscribe_CanUseNullNamespace()
    {
        var subscriptionManager = new SubscriptionManager(this.fixture.HostedCluster);
        var streamId = new FullStreamIdentity(Guid.NewGuid(), null, StreamProviderName);
        await subscriptionManager.AddSubscription<IPassive_ConsumerGrain>(streamId,
            Guid.NewGuid());
        var subscriptions = await subscriptionManager.GetSubscriptions(streamId);
        await subscriptionManager.RemoveSubscription(streamId, subscriptions.First().SubscriptionId);
    }

    [SkippableFact]
    public async Task StreamingTests_Consumer_Producer_Subscribe()
    {
        using var observer = StreamingDiagnosticObserver.Create();
        using var cts = new CancellationTokenSource(_timeout);
        var subscriptionManager = new SubscriptionManager(this.fixture.HostedCluster);
        var streamId = new FullStreamIdentity(Guid.NewGuid(), "EmptySpace", StreamProviderName);
        var rxStreamId = StreamId.Create(streamId.Namespace, streamId.Guid);
        //set up subscription for 10 consumer grains
        var subscriptions = await subscriptionManager.SetupStreamingSubscriptionForStream<IPassive_ConsumerGrain>(streamId, 10);
        var consumers = subscriptions.Select(sub => this.fixture.HostedCluster.GrainFactory.GetGrain<IPassive_ConsumerGrain>(sub.GrainId)).ToList();

        var producer = this.fixture.HostedCluster.GrainFactory.GetGrain<ITypedProducerGrainProducingApple>(Guid.NewGuid());
        await producer.BecomeProducer(streamId.Guid, streamId.Namespace, streamId.ProviderName);

        await ProduceExactCountAsync(producer, EventCountPerPhase);
        await observer.WaitForItemDeliveryCountAsync(rxStreamId, EventCountPerPhase * consumers.Count, StreamProviderName, cts.Token);
        await AssertCountersAsync(new[] { producer }, consumers, this.fixture.Logger);

        //clean up test
        await Task.WhenAll(consumers.Select(consumer => consumer.StopConsuming()));
    }

    [SkippableFact(Skip = "https://github.com/dotnet/orleans/issues/5635")]
    public async Task StreamingTests_Consumer_Producer_UnSubscribe()
    {
        using var cts = new CancellationTokenSource(_timeout);
        var subscriptionManager = new SubscriptionManager(this.fixture.HostedCluster);
        var streamId = new FullStreamIdentity(Guid.NewGuid(), "EmptySpace", StreamProviderName);
        var rxStreamId = StreamId.Create(streamId.Namespace, streamId.Guid);
        //set up subscription for consumer grains
        var subscriptions = await subscriptionManager.SetupStreamingSubscriptionForStream<IPassive_ConsumerGrain>(streamId, 2);

        var producer = this.fixture.GrainFactory.GetGrain<ITypedProducerGrainProducingApple>(Guid.NewGuid());
        await producer.BecomeProducer(streamId.Guid, streamId.Namespace, streamId.ProviderName);

        using (var phaseOneObserver = StreamingDiagnosticObserver.Create())
        {
            await ProduceExactCountAsync(producer, EventCountPerPhase);
            await phaseOneObserver.WaitForItemDeliveryCountAsync(rxStreamId, EventCountPerPhase * subscriptions.Count, StreamProviderName, cts.Token);
        }

        //the subscription to remove
        var subscription = subscriptions[0];
        // remove subscription
        using (var removalObserver = StreamingDiagnosticObserver.Create())
        {
            await subscriptionManager.RemoveSubscription(streamId, subscription.SubscriptionId);
            await removalObserver.WaitForSubscriptionRemovedAsync(rxStreamId, StreamProviderName, cts.Token);
        }

        var consumerUnSub = this.fixture.GrainFactory.GetGrain<IPassive_ConsumerGrain>(subscription.GrainId);
        var consumerNormal = this.fixture.GrainFactory.GetGrain<IPassive_ConsumerGrain>(subscriptions[1].GrainId);
        //assert consumer grain's onAdd func got called.
        Assert.True((await consumerUnSub.GetCountOfOnAddFuncCalled()) > 0);

        using (var phaseTwoObserver = StreamingDiagnosticObserver.Create())
        {
            await ProduceExactCountAsync(producer, EventCountPerPhase);
            await phaseTwoObserver.WaitForItemDeliveryCountAsync(rxStreamId, EventCountPerPhase, StreamProviderName, cts.Token);
        }

        await AssertCountersAsync(new[] { producer }, consumerNormal, this.fixture.Logger);

        //asert unsubscribed consumer consumed less than produced
        var numProduced = await producer.GetNumberProduced();
        var numConsumed = await consumerUnSub.GetNumberConsumed();
        Assert.Equal(EventCountPerPhase, numConsumed);
        Assert.True(numConsumed < numProduced);

        // clean up test
        await consumerNormal.StopConsuming();
        await consumerUnSub.StopConsuming();
    }

    [SkippableFact]
    public async Task StreamingTests_Consumer_Producer_GetSubscriptions()
    {
        var subscriptionManager = new SubscriptionManager(this.fixture.HostedCluster);
        var streamId = new FullStreamIdentity(Guid.NewGuid(), "EmptySpace", StreamProviderName);
        //set up subscriptions
        var expectedSubscriptions = await subscriptionManager.SetupStreamingSubscriptionForStream<IPassive_ConsumerGrain>(streamId, 2);
        var expectedSubscriptionIds = expectedSubscriptions.Select(sub => sub.SubscriptionId).ToSet();
        var subscriptions = await subscriptionManager.GetSubscriptions(streamId);
        var subscriptionIds = subscriptions.Select(sub => sub.SubscriptionId).ToSet();
        Assert.True(expectedSubscriptionIds.SetEquals(subscriptionIds));

         //remove one subscription
        await subscriptionManager.RemoveSubscription(streamId, expectedSubscriptions[0].SubscriptionId);
        expectedSubscriptions = expectedSubscriptions.GetRange(1, 1);
        subscriptions = await subscriptionManager.GetSubscriptions(streamId);
        expectedSubscriptionIds = expectedSubscriptions.Select(sub => sub.SubscriptionId).ToSet();
        subscriptionIds = subscriptions.Select(sub => sub.SubscriptionId).ToSet();
        Assert.True(expectedSubscriptionIds.SetEquals(subscriptionIds));

        // clean up tests
    }

    [SkippableFact]
    public async Task StreamingTests_Consumer_Producer_ConsumerUnsubscribeOnAdd()
    {
        var subscriptionManager = new SubscriptionManager(this.fixture.HostedCluster);
        var streamId = new FullStreamIdentity(Guid.NewGuid(), "EmptySpace", StreamProviderName);
        //set up subscriptions
        await subscriptionManager.SetupStreamingSubscriptionForStream<IJerk_ConsumerGrain>(streamId, 10);

        using var observer = StreamingDiagnosticObserver.Create();
        using var cts = new CancellationTokenSource(_timeout);

        //producer start producing 
        var producer = this.fixture.GrainFactory.GetGrain<ITypedProducerGrainProducingInt>(Guid.NewGuid());
        await producer.BecomeProducer(streamId.Guid, streamId.Namespace, streamId.ProviderName);

        await ProduceExactCountAsync(producer, 1);

        //wait for consumers to unsubscribe via diagnostic events
        var rxStreamId = StreamId.Create("EmptySpace", streamId.Guid);
        await observer.WaitForSubscriptionRemovedCountAsync(rxStreamId, 10, StreamProviderName, cts.Token);

        var subs = await subscriptionManager.GetSubscriptions(streamId);
        Assert.Empty(subs);
        // clean up tests
    }


    [SkippableFact(Skip = "https://github.com/dotnet/orleans/issues/5650")]
    public async Task StreamingTests_Consumer_Producer_SubscribeToTwoStream_MessageWithPolymorphism()
    {
        using var observer = StreamingDiagnosticObserver.Create();
        using var cts = new CancellationTokenSource(_timeout);
        var subscriptionManager = new SubscriptionManager(this.fixture.HostedCluster);
        var streamId = new FullStreamIdentity(Guid.NewGuid(), "EmptySpace", StreamProviderName);
        var rxStreamId = StreamId.Create(streamId.Namespace, streamId.Guid);
        //set up subscription for 10 consumer grains
        var subscriptions = await subscriptionManager.SetupStreamingSubscriptionForStream<IPassive_ConsumerGrain>(streamId, 10);
        var consumers = subscriptions.Select(sub => this.fixture.GrainFactory.GetGrain<IPassive_ConsumerGrain>(sub.GrainId)).ToList();

        var producer = this.fixture.GrainFactory.GetGrain<ITypedProducerGrainProducingApple>(Guid.NewGuid());
        await producer.BecomeProducer(streamId.Guid, streamId.Namespace, streamId.ProviderName);

        // set up the new stream to subscribe, which produce strings
        var streamId2 = new FullStreamIdentity(Guid.NewGuid(), "EmptySpace2", StreamProviderName);
        var rxStreamId2 = StreamId.Create(streamId2.Namespace, streamId2.Guid);
        var producer2 = this.fixture.GrainFactory.GetGrain<ITypedProducerGrainProducingApple>(Guid.NewGuid());
        await producer2.BecomeProducer(streamId2.Guid, streamId2.Namespace, streamId2.ProviderName);

        //register the consumer grain to second stream
        var tasks = consumers.Select(consumer => subscriptionManager.AddSubscription<IPassive_ConsumerGrain>(streamId2, consumer.GetPrimaryKey())).ToList();
        await Task.WhenAll(tasks);

        for (var i = 0; i < EventCountPerPhase; i++)
        {
            await producer.Produce();
            if (i < EventCountPerPhase - 2)
            {
                await producer2.Produce();
            }
        }
        await Task.WhenAll(
            observer.WaitForItemDeliveryCountAsync(rxStreamId, EventCountPerPhase * consumers.Count, StreamProviderName, cts.Token),
            observer.WaitForItemDeliveryCountAsync(rxStreamId2, (EventCountPerPhase - 2) * consumers.Count, StreamProviderName, cts.Token));
        await AssertCountersAsync(new ITypedProducerGrain[] { producer, producer2 }, consumers, this.fixture.Logger);

        //clean up test
        await Task.WhenAll(consumers.Select(consumer => consumer.StopConsuming()));
    }

    [SkippableFact]
    public async Task StreamingTests_Consumer_Producer_SubscribeToStreamsHandledByDifferentStreamProvider()
    {
        using var observer = StreamingDiagnosticObserver.Create();
        using var cts = new CancellationTokenSource(_timeout);
        var subscriptionManager = new SubscriptionManager(this.fixture.HostedCluster);
        var streamId = new FullStreamIdentity(Guid.NewGuid(), "EmptySpace", StreamProviderName);
        var rxStreamId = StreamId.Create(streamId.Namespace, streamId.Guid);
        //set up subscription for 10 consumer grains
        var subscriptions = await subscriptionManager.SetupStreamingSubscriptionForStream<IPassive_ConsumerGrain>(streamId, 10);
        var consumers = subscriptions.Select(sub => this.fixture.GrainFactory.GetGrain<IPassive_ConsumerGrain>(sub.GrainId)).ToList();

        var producer = this.fixture.GrainFactory.GetGrain<ITypedProducerGrainProducingApple>(Guid.NewGuid());
        await producer.BecomeProducer(streamId.Guid, streamId.Namespace, streamId.ProviderName);

        // set up the new stream to subscribe, which produce strings
        var streamId2 = new FullStreamIdentity(Guid.NewGuid(), "EmptySpace2", StreamProviderName2);
        var rxStreamId2 = StreamId.Create(streamId2.Namespace, streamId2.Guid);
        var producer2 = this.fixture.GrainFactory.GetGrain<ITypedProducerGrainProducingApple>(Guid.NewGuid());
        await producer2.BecomeProducer(streamId2.Guid, streamId2.Namespace, streamId2.ProviderName);

        //register the consumer grain to second stream
        var tasks = consumers.Select(consumer => subscriptionManager.AddSubscription<IPassive_ConsumerGrain>(streamId2, consumer.GetPrimaryKey())).ToList();
        await Task.WhenAll(tasks);

        for (var i = 0; i < EventCountPerPhase; i++)
        {
            await producer.Produce();
            if (i < EventCountPerPhase - 2)
            {
                await producer2.Produce();
            }
        }
        await Task.WhenAll(
            observer.WaitForItemDeliveryCountAsync(rxStreamId, EventCountPerPhase * consumers.Count, StreamProviderName, cts.Token),
            observer.WaitForItemDeliveryCountAsync(rxStreamId2, (EventCountPerPhase - 2) * consumers.Count, StreamProviderName2, cts.Token));
        await AssertCountersAsync(new ITypedProducerGrain[] { producer, producer2 }, consumers, this.fixture.Logger);

        //clean up test
        await Task.WhenAll(consumers.Select(consumer => consumer.StopConsuming()));
    }

    //test utilities and statics
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

    private static async Task ProduceExactCountAsync(ITypedProducerGrain producer, int count)
    {
        for (var i = 0; i < count; i++)
        {
            await producer.Produce();
        }
    }

    private static async Task AssertCountersAsync(IEnumerable<ITypedProducerGrain> producers, IEnumerable<IPassive_ConsumerGrain> consumers, ILogger logger)
    {
        foreach (var consumer in consumers)
        {
            await AssertCountersAsync(producers, consumer, logger);
        }
    }

    private static async Task AssertCountersAsync(IEnumerable<ITypedProducerGrain> producers, IPassive_ConsumerGrain consumer, ILogger logger)
    {
        int numProduced = 0;
        foreach (var p in producers)
        {
            numProduced += await p.GetNumberProduced();
        }
        var numConsumed = await consumer.GetNumberConsumed();
        logger.LogInformation("CheckCounters: numProduced = {ProducedCount}, numConsumed = {ConsumedCount}", numProduced, numConsumed);
        Assert.Equal(numProduced, numConsumed);
    }
}

public class SubscriptionManager
{
    private readonly IGrainFactory grainFactory;
    private readonly IServiceProvider serviceProvider;
    private readonly IStreamSubscriptionManager subManager;
    public SubscriptionManager(TestCluster cluster)
    {
        this.grainFactory = cluster.GrainFactory;
        this.serviceProvider = cluster.ServiceProvider;
        var admin = serviceProvider.GetRequiredService<IStreamSubscriptionManagerAdmin>();
        this.subManager = admin.GetStreamSubscriptionManager(StreamSubscriptionManagerType.ExplicitSubscribeOnly);
    }

    public async Task<List<StreamSubscription>> SetupStreamingSubscriptionForStream<TGrainInterface>(FullStreamIdentity streamIdentity, int grainCount)
        where TGrainInterface : IGrainWithGuidKey
    {
        var subscriptions = new List<StreamSubscription>();
        while (grainCount > 0)
        {
            var grainId = Guid.NewGuid();
            var grainRef = (this.grainFactory.GetGrain<TGrainInterface>(grainId) as GrainReference)!;
            subscriptions.Add(await subManager.AddSubscription(streamIdentity.ProviderName, streamIdentity, grainRef));
            grainCount--;
        }
        return subscriptions;
    }

    public async Task<StreamSubscription> AddSubscription<TGrainInterface>(FullStreamIdentity streamId, Guid grainId)
        where TGrainInterface : IGrainWithGuidKey
    {
        var grainRef = (this.grainFactory.GetGrain<TGrainInterface>(grainId) as GrainReference)!;
        var sub = await this.subManager
            .AddSubscription(streamId.ProviderName, streamId, grainRef);
        return sub;
    }

    public Task<IEnumerable<StreamSubscription>> GetSubscriptions(FullStreamIdentity streamIdentity)
    {
        return subManager.GetSubscriptions(streamIdentity.ProviderName, streamIdentity);
    }

    public async Task RemoveSubscription(FullStreamIdentity streamId, Guid subscriptionId)
    {
        await subManager.RemoveSubscription(streamId.ProviderName, streamId, subscriptionId);
    }
}
