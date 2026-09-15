using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace UnitTests.StreamingTests;

public partial class PubSubRendezvousGrainTests
{
    [Fact, TestCategory("BVT"), TestCategory("Streaming"), TestCategory("PubSub")]
    public async Task ProducerRetirement_PersistsRemovalAndNotifiesOnlyOtherProducer_OrdinaryRemovalNotifiesBoth()
    {
        await using var scenario = await RetirementScenario.Create(fixture);
        var cancellationToken = TestContext.Current.CancellationToken;
        var ordinarySubscription = GuidId.GetGuidId(Guid.NewGuid());
        await scenario.PubSub.RegisterConsumer(ordinarySubscription, scenario.StreamId, default, null, cancellationToken);

        await AwaitRetirementPhase(scenario.Retire(cancellationToken), "producer-originated removal");

        await scenario.AssertDurableConsumers(ordinarySubscription);
        Assert.Empty(scenario.Requester.RemovalAttempts);
        AssertRemoval(scenario.Other, scenario.SubscriptionId, scenario.StreamId);

        // Validate reloaded durable state above. Neither producer is re-registered here:
        // ordinary removal must still reach the exact requester, not merely retain a count.
        await AwaitRetirementPhase(
            scenario.PubSub.UnregisterConsumer(ordinarySubscription, scenario.StreamId, cancellationToken),
            "ordinary removal notifying both retained producers");

        AssertRemoval(scenario.Requester, ordinarySubscription, scenario.StreamId);
        Assert.Equal(
            new[] { (scenario.SubscriptionId, scenario.StreamId), (ordinarySubscription, scenario.StreamId) },
            scenario.Other.Removals.ToArray());
        await scenario.AssertDurableConsumers();
    }

    [Fact, TestCategory("BVT"), TestCategory("Streaming"), TestCategory("PubSub")]
    public async Task ProducerRetirement_WriteFailurePreservesOriginalErrorAndSkipsNotifications_RetryPersistsRemoval()
    {
        await using var scenario = await RetirementScenario.Create(fixture);
        var cancellationToken = TestContext.Current.CancellationToken;
        var faultGrain = fixture.GrainFactory.GetGrain<IStorageFaultGrain>(nameof(PubSubRendezvousGrain));
        const string failureMessage = "producer retirement durable write failed";
        using var observer = GrainDiagnosticObserver.Create(fixture.HostedCluster);
        var deactivated = observer.WaitForGrainDeactivatedAsync(scenario.PubSub.GetGrainId());
        await faultGrain.AddFaultOnWrite(scenario.PubSub.GetGrainId(), new ApplicationException(failureMessage));

        var exception = await Assert.ThrowsAsync<OrleansException>(
            () => AwaitRetirementPhase(scenario.Retire(cancellationToken), "injected retirement write failure"));

        Assert.Equal(failureMessage, Assert.IsType<ApplicationException>(exception.InnerException).Message);
        Assert.Empty(scenario.Requester.RemovalAttempts);
        Assert.Empty(scenario.Other.RemovalAttempts);
        Assert.Empty(scenario.Other.Removals);

        // Do not inspect the failed activation's mutated in-memory state. Wait for its
        // actual deactivation, then prove the next activation reads the original consumer.
        await AwaitRetirementPhase(deactivated, $"failed rendezvous {scenario.PubSub.GetGrainId()} deactivation");
        var consumer = Assert.Single(await scenario.PubSub.DiagGetConsumers(scenario.StreamId, cancellationToken));
        Assert.Equal(scenario.SubscriptionId, consumer.SubscriptionId);
        Assert.Equal(scenario.StreamId, consumer.Stream);
        Assert.Equal(2, await scenario.PubSub.ProducerCount(scenario.StreamId, cancellationToken));

        await AwaitRetirementPhase(scenario.Retire(cancellationToken), "retirement retry after storage failure");

        await scenario.AssertDurableConsumers();
        Assert.Empty(scenario.Requester.RemovalAttempts);
        AssertRemoval(scenario.Other, scenario.SubscriptionId, scenario.StreamId);
        Assert.Single(scenario.Other.RemovalAttempts);
    }

    [Fact, TestCategory("BVT"), TestCategory("Streaming"), TestCategory("PubSub")]
    public async Task ProducerRetirement_AbsentConsumerRetriesFailedAndSuccessfulNotificationsWithoutCallingRequester()
    {
        await using var scenario = await RetirementScenario.Create(fixture);
        var cancellationToken = TestContext.Current.CancellationToken;
        const string failureMessage = "other producer removal failed";
        scenario.Other.NextRemovalFailure = new ApplicationException(failureMessage);
        using var observer = GrainDiagnosticObserver.Create(fixture.HostedCluster);
        var deactivated = observer.WaitForGrainDeactivatedAsync(scenario.PubSub.GetGrainId());

        var exception = await Assert.ThrowsAsync<ApplicationException>(
            () => AwaitRetirementPhase(scenario.Retire(cancellationToken), "injected producer notification failure"));

        Assert.Equal(failureMessage, exception.Message);
        Assert.Empty(scenario.Requester.RemovalAttempts);
        Assert.Empty(scenario.Other.Removals);
        Assert.Equal((scenario.SubscriptionId, scenario.StreamId), Assert.Single(scenario.Other.RemovalAttempts));
        await AwaitRetirementPhase(deactivated, $"rendezvous {scenario.PubSub.GetGrainId()} notification-failure deactivation");
        await scenario.AssertDurableConsumers();

        // The durable subscription is already absent, but retry must repair notification.
        await AwaitRetirementPhase(scenario.Retire(cancellationToken), "absent-consumer notification retry");
        AssertRemoval(scenario.Other, scenario.SubscriptionId, scenario.StreamId);
        await AwaitRetirementPhase(scenario.Retire(cancellationToken), "repeat removal after successful retry");

        Assert.Empty(scenario.Requester.RemovalAttempts);
        Assert.Equal(
            Enumerable.Repeat((scenario.SubscriptionId, scenario.StreamId), 3),
            scenario.Other.RemovalAttempts.ToArray());
        Assert.Equal(
            Enumerable.Repeat((scenario.SubscriptionId, scenario.StreamId), 2),
            scenario.Other.Removals.ToArray());
        await scenario.AssertDurableConsumers();
    }

    [Fact, TestCategory("BVT"), TestCategory("Streaming"), TestCategory("PubSub")]
    public async Task ProducerRetirement_OnDeactivateAsyncCompletesWithoutCallbackCycleAndRetainsStableProducer()
    {
        await using var scenario = await RetirementScenario.Create(fixture, holdOtherNotification: true);
        var cancellationToken = TestContext.Current.CancellationToken;
        await AwaitRetirementPhase(
            scenario.RequesterReference.RetireOnDeactivation(scenario.SubscriptionId, scenario.StreamId),
            "arming producer retirement during deactivation");
        var context = scenario.RequesterContext;
        var deactivated = context.Deactivated;
        var retirementEntered = scenario.Requester.RetirementEntered.Task;
        var otherNotified = scenario.Other.RemovalEntered.Task;

        try
        {
            context.Deactivate(new DeactivationReason(DeactivationReasonCode.ApplicationRequested, "pubsub retirement regression"), cancellationToken);
            await AwaitRetirementPhase(retirementEntered, $"producer {context.GrainId} OnDeactivateAsync entry");
            Assert.False(deactivated.IsCompleted);
            Assert.Equal(1, await scenario.PubSub.ConsumerCount(scenario.StreamId, cancellationToken));
            Assert.Equal(2, await scenario.PubSub.ProducerCount(scenario.StreamId, cancellationToken));

            scenario.Requester.ContinueRetirement.TrySetResult();
            await AwaitRetirementPhase(otherNotified, $"stable producer {scenario.OtherContext.GrainId} notification");
            Assert.Equal((scenario.SubscriptionId, scenario.StreamId), Assert.Single(scenario.Other.RemovalAttempts));
            Assert.Empty(scenario.Requester.RemovalAttempts);
            Assert.False(deactivated.IsCompleted);
            Assert.False(scenario.Requester.RetirementSucceeded);

            scenario.Other.ContinueRemoval.TrySetResult();
            await AwaitRetirementPhase(deactivated, $"exact producer activation {context.Address} deactivation");

            // The runtime can finish deactivation even if OnDeactivateAsync throws.
            // Require successful retirement as well as completion of this exact context.
            Assert.Null(scenario.Requester.RetirementError);
            Assert.True(scenario.Requester.RetirementSucceeded);
            Assert.Empty(scenario.Requester.RemovalAttempts);
            AssertRemoval(scenario.Other, scenario.SubscriptionId, scenario.StreamId);
            Assert.False(scenario.OtherContext.Deactivated.IsCompleted);
            await scenario.AssertDurableConsumers();
        }
        finally
        {
            scenario.ReleaseBarriers();
        }
    }

    [Fact, TestCategory("BVT"), TestCategory("Streaming"), TestCategory("PubSub")]
    public async Task ProducerRetirement_BuiltinExplicitFacadesForwardExactIdsAndCancellationToken()
    {
        var grainFactory = Substitute.For<IGrainFactory>();
        var rendezvous = Substitute.For<IPubSubRendezvousGrain>();
        var streamId = new QualifiedStreamId("RetirementProvider", StreamId.Create("RetirementNamespace", Guid.NewGuid()));
        var subscriptionId = GuidId.GetGuidId(SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid()));
        var producer = fixture.GrainFactory.GetGrain<IPubSubRetirementTestProducer>(Guid.NewGuid()).GetGrainId();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        grainFactory.GetGrain<IPubSubRendezvousGrain>(streamId.ToString(), null).Returns(rendezvous);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rendezvous.UnregisterConsumerFromProducer(subscriptionId, streamId, producer, cancellation.Token).Returns(completion.Task);
        var runtime = new GrainBasedPubSubRuntime(grainFactory);
        var implicitPubSub = new ImplicitStreamPubSub(fixture.HostedCluster.GetSiloServiceProvider().GetRequiredService<ImplicitStreamSubscriberTable>());
        var facade = new StreamPubSubImpl(runtime, implicitPubSub);

        try
        {
            var removal = facade.UnregisterConsumerFromProducer(subscriptionId, streamId, producer, cancellation.Token);
            Assert.Same(completion.Task, removal);
            Assert.False(removal.IsCompleted);
            completion.SetResult();
            await AwaitRetirementPhase(removal, "built-in explicit retirement forwarding");

            _ = grainFactory.Received(1).GetGrain<IPubSubRendezvousGrain>(streamId.ToString(), null);
            await rendezvous.Received(1).UnregisterConsumerFromProducer(subscriptionId, streamId, producer, cancellation.Token);
            await rendezvous.DidNotReceiveWithAnyArgs().UnregisterConsumer(default!, default, Arg.Any<CancellationToken>());
        }
        finally
        {
            completion.TrySetResult();
        }
    }

    [Fact, TestCategory("BVT"), TestCategory("Streaming"), TestCategory("PubSub")]
    public async Task ProducerRetirement_ImplicitFacadeKeepsValidationAndCancellationWithoutCallingExplicitPubSub()
    {
        var explicitPubSub = Substitute.For<IStreamPubSubRuntime>();
        var implicitPubSub = new ImplicitStreamPubSub(fixture.HostedCluster.GetSiloServiceProvider().GetRequiredService<ImplicitStreamSubscriberTable>());
        var facade = new StreamPubSubImpl(explicitPubSub, implicitPubSub);
        var streamId = new QualifiedStreamId("RetirementProvider", StreamId.Create("RetirementNamespace", Guid.NewGuid()));
        var producer = fixture.GrainFactory.GetGrain<IPubSubRetirementTestProducer>(Guid.NewGuid()).GetGrainId();
        var implicitId = GuidId.GetGuidId(SubscriptionMarker.MarkAsImplictSubscriptionId(Guid.NewGuid()));
        var explicitId = GuidId.GetGuidId(SubscriptionMarker.MarkAsExplicitSubscriptionId(Guid.NewGuid()));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        await facade.UnregisterConsumerFromProducer(implicitId, streamId, producer, cancellation.Token);
        var invalid = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => implicitPubSub.UnregisterConsumerFromProducer(explicitId, streamId, producer, cancellation.Token));
        Assert.Equal(streamId.ToString(), invalid.ParamName);
        Assert.Contains("does not support explicit subscriptions", invalid.Message);

        cancellation.Cancel();
        var canceled = await Assert.ThrowsAsync<OperationCanceledException>(
            () => facade.UnregisterConsumerFromProducer(implicitId, streamId, producer, cancellation.Token));
        Assert.Equal(cancellation.Token, canceled.CancellationToken);
        Assert.Empty(explicitPubSub.ReceivedCalls());
    }

    private static void AssertRemoval(PubSubRetirementTestProducer producer, GuidId subscriptionId, QualifiedStreamId streamId)
        => Assert.Equal((subscriptionId, streamId), Assert.Single(producer.Removals));

    private static async Task AwaitRetirementPhase(Task task, string phase)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"Pubsub producer retirement timed out waiting for {phase}.", exception);
        }
    }

    private sealed class RetirementScenario : IAsyncDisposable
    {
        public QualifiedStreamId StreamId { get; } = new("RetirementProvider", Orleans.Runtime.StreamId.Create("RetirementNamespace", Guid.NewGuid()));
        public GuidId SubscriptionId { get; } = GuidId.GetGuidId(Guid.NewGuid());
        public IPubSubRendezvousGrain PubSub { get; }
        public IPubSubRetirementTestProducer RequesterReference { get; }
        private IPubSubRetirementTestProducer OtherReference { get; }
        public IGrainContext RequesterContext { get; private set; } = null!;
        public IGrainContext OtherContext { get; private set; } = null!;
        public PubSubRetirementTestProducer Requester { get; private set; } = null!;
        public PubSubRetirementTestProducer Other { get; private set; } = null!;

        private RetirementScenario(Fixture fixture)
        {
            PubSub = fixture.GrainFactory.GetGrain<IPubSubRendezvousGrain>(StreamId.ToString());
            RequesterReference = fixture.GrainFactory.GetGrain<IPubSubRetirementTestProducer>(Guid.NewGuid());
            OtherReference = fixture.GrainFactory.GetGrain<IPubSubRetirementTestProducer>(Guid.NewGuid());
        }

        public static async Task<RetirementScenario> Create(Fixture fixture, bool holdOtherNotification = false)
        {
            var result = new RetirementScenario(fixture);
            using var observer = GrainDiagnosticObserver.Create(fixture.HostedCluster);
            var requesterActivated = observer.WaitForGrainActivatedAsync(result.RequesterReference.GetGrainId());
            var otherActivated = observer.WaitForGrainActivatedAsync(result.OtherReference.GetGrainId());
            await AwaitRetirementPhase(
                Task.WhenAll(result.RequesterReference.Activate(), result.OtherReference.Activate()), "both producer activations");
            await AwaitRetirementPhase(Task.WhenAll(requesterActivated, otherActivated), "producer activation diagnostics");
            result.RequesterContext = (await requesterActivated).GrainContext;
            result.OtherContext = (await otherActivated).GrainContext;
            result.Requester = Assert.IsType<PubSubRetirementTestProducer>(result.RequesterContext.GrainInstance);
            result.Other = Assert.IsType<PubSubRetirementTestProducer>(result.OtherContext.GrainInstance);
            if (holdOtherNotification)
            {
                result.Other.ContinueRemoval = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            try
            {
                var cancellationToken = TestContext.Current.CancellationToken;
                // Register before producers so setup cannot be confused with removal callbacks.
                await result.PubSub.RegisterConsumer(result.SubscriptionId, result.StreamId, default, null, cancellationToken);
                var requesterSubscriptions = await result.PubSub.RegisterProducer(result.StreamId, result.RequesterContext.GrainId, cancellationToken);
                var otherSubscriptions = await result.PubSub.RegisterProducer(result.StreamId, result.OtherContext.GrainId, cancellationToken);
                Assert.Equal(result.SubscriptionId, Assert.Single(requesterSubscriptions).SubscriptionId);
                Assert.Equal(result.SubscriptionId, Assert.Single(otherSubscriptions).SubscriptionId);
                return result;
            }
            catch
            {
                await result.DisposeAsync();
                throw;
            }
        }

        public Task Retire(CancellationToken cancellationToken)
            => PubSub.UnregisterConsumerFromProducer(SubscriptionId, StreamId, RequesterContext.GrainId, cancellationToken);

        public async Task AssertDurableConsumers(params GuidId[] subscriptionIds)
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            // Validate reads storage and compares complete producer/consumer sets with memory.
            await PubSub.Validate(cancellationToken);
            var consumers = await PubSub.DiagGetConsumers(StreamId, cancellationToken);
            Assert.Equal(subscriptionIds, consumers.Select(consumer => consumer.SubscriptionId));
            Assert.All(consumers, consumer => Assert.Equal(StreamId, consumer.Stream));
            Assert.Equal(2, await PubSub.ProducerCount(StreamId, cancellationToken));
        }

        public void ReleaseBarriers()
        {
            foreach (var producer in new[] { Requester, Other })
            {
                producer.ContinueRetirement.TrySetResult();
                producer.ContinueRemoval.TrySetResult();
                producer.AbortRetirement.Cancel();
            }
        }

        public async ValueTask DisposeAsync()
        {
            ReleaseBarriers();
            try
            {
                var cancellationToken = TestContext.Current.CancellationToken;
                await AwaitRetirementPhase(PubSub.UnregisterProducer(StreamId, RequesterContext.GrainId, cancellationToken), "requester registration cleanup");
                await AwaitRetirementPhase(PubSub.UnregisterProducer(StreamId, OtherContext.GrainId, cancellationToken), "other producer registration cleanup");
                foreach (var consumer in await PubSub.DiagGetConsumers(StreamId, cancellationToken))
                {
                    await AwaitRetirementPhase(PubSub.UnregisterConsumer(consumer.SubscriptionId, StreamId, cancellationToken), "remaining consumer cleanup");
                }
                await AwaitRetirementPhase(PubSub.UnregisterConsumer(SubscriptionId, StreamId, cancellationToken), "subscription cleanup");
            }
            finally
            {
                var reason = new DeactivationReason(DeactivationReasonCode.ApplicationRequested, "pubsub retirement test cleanup");
                RequesterContext.Deactivate(reason);
                OtherContext.Deactivate(reason);
                await AwaitRetirementPhase(Task.WhenAll(RequesterContext.Deactivated, OtherContext.Deactivated), "producer activation cleanup");
                Requester.AbortRetirement.Dispose();
                Other.AbortRetirement.Dispose();
            }
        }
    }
}

public interface IPubSubRetirementTestProducer : IGrainWithGuidKey
{
    Task Activate();
    Task RetireOnDeactivation(GuidId subscriptionId, QualifiedStreamId streamId);
}

// Real activation, not a local fake: the extension's AlwaysInterleave methods cannot
// be dispatched back into this activation while OnDeactivateAsync awaits retirement.
public sealed class PubSubRetirementTestProducer : Grain, IPubSubRetirementTestProducer, IStreamProducerExtension
{
    private GuidId? _retiringSubscription;
    private QualifiedStreamId _retiringStream;
    internal ConcurrentQueue<(GuidId, QualifiedStreamId)> RemovalAttempts { get; } = new();
    internal ConcurrentQueue<(GuidId, QualifiedStreamId)> Removals { get; } = new();
    internal TaskCompletionSource RemovalEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource ContinueRemoval { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource RetirementEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource ContinueRetirement { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal CancellationTokenSource AbortRetirement { get; } = new();
    internal Exception? NextRemovalFailure;
    internal Exception? RetirementError { get; private set; }
    internal bool RetirementSucceeded { get; private set; }

    // A replacement activation must not inherit a test barrier if a regressed callback
    // is rerouted here after cleanup cancels the original activation's retirement wait.
    public PubSubRetirementTestProducer() => ContinueRemoval.SetResult();

    public Task Activate() => Task.CompletedTask;

    public Task RetireOnDeactivation(GuidId subscriptionId, QualifiedStreamId streamId)
    {
        _retiringSubscription = subscriptionId;
        _retiringStream = streamId;
        return Task.CompletedTask;
    }

    public Task AddSubscriber(GuidId subscriptionId, QualifiedStreamId streamId, GrainId streamConsumer, string? filterData, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public async Task RemoveSubscriber(GuidId subscriptionId, QualifiedStreamId streamId, CancellationToken cancellationToken)
    {
        RemovalAttempts.Enqueue((subscriptionId, streamId));
        RemovalEntered.TrySetResult();
        await ContinueRemoval.Task.WaitAsync(cancellationToken);
        if (Interlocked.Exchange(ref NextRemovalFailure, null) is { } exception)
        {
            throw exception;
        }

        Removals.Enqueue((subscriptionId, streamId));
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        if (_retiringSubscription is not { } subscriptionId)
        {
            return;
        }

        RetirementEntered.TrySetResult();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, AbortRetirement.Token);
        try
        {
            await ContinueRetirement.Task.WaitAsync(cancellation.Token);
            var pubSub = GrainFactory.GetGrain<IPubSubRendezvousGrain>(_retiringStream.ToString());
            // The local wait is cancelable by test cleanup even if a regressed RPC is
            // blocked in a callback cycle, so a failed test does not wedge its fixture.
            await pubSub.UnregisterConsumerFromProducer(subscriptionId, _retiringStream, GrainContext.GrainId, cancellation.Token)
                .WaitAsync(cancellation.Token);
            RetirementSucceeded = true;
        }
        catch (Exception exception)
        {
            RetirementError = exception;
            throw;
        }
    }
}
