using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Concurrency;
using Orleans.Configuration;
using Orleans.Placement;
using Orleans.Runtime;
using Orleans.Runtime.Placement;
using Orleans.Runtime.Scheduler;
using Orleans.Serialization.Session;
using Orleans.TestingHost;
using Xunit;

namespace UnitTests.ActivationsLifeCycleTests;

/// <summary>
/// Verifies that a retiring silo migrates an activation which has queued requests, together with those requests,
/// and that callers whose requests moved keep waiting when the retiring silo departs.
/// </summary>
[TestSuite("BVT"), TestProvider("None"), TestCategory("BVT")]
public sealed class RetirementHandoffTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedRequest_MigratesWithActivation_AndSurvivesRetiringSiloDeparture(bool directoryOwnedByRetiringSilo)
    {
        var builder = new InProcessTestClusterBuilder(3);
        await using var cluster = builder.Build();
        await cluster.DeployAsync(Cancellation);
        await cluster.WaitForLivenessToStabilizeAsync().WaitAsync(Cancellation);
        await cluster.WaitForClusterManifestToStabilizeAsync().WaitAsync(Cancellation);

        var retiring = cluster.Silos[1];
        var caller = cluster.Silos[0];
        var grainFactory = caller.ServiceProvider.GetRequiredService<IGrainFactory>();
        var key = SelectKey(grainFactory, caller, retiring, directoryOwnedByRetiringSilo);
        var state = RetirementHandoffState.Create(key);
        var grain = grainFactory.GetGrain<IRetirementHandoffGrain>(key, typeof(RetirementHandoffGrain).FullName);

        RequestContext.Set(IPlacementDirector.PlacementHintKey, retiring.SiloAddress);
        try
        {
            Assert.Equal(retiring.SiloAddress, await grain.GetLocation().AsTask().WaitAsync(Timeout, Cancellation));
        }
        finally
        {
            RequestContext.Remove(IPlacementDirector.PlacementHintKey);
        }

        // Occupy the activation, then queue a request behind the running one.
        var running = grain.Block().AsTask();
        await state.BlockEntered.Task.WaitAsync(Timeout, Cancellation);
        var queued = grain.HoldThenGetLocation().AsTask();
        await WaitUntilAsync(() => GetWaitingCount(retiring, grain.GetGrainId()) == 1);

        var stopping = cluster.StopSiloAsync(retiring, Cancellation);
        await WaitUntilAsync(() => FindActivation(retiring, grain.GetGrainId()) is ActivationData { State: ActivationState.Deactivating });
        state.ReleaseBlock.TrySetResult();
        await running.WaitAsync(Timeout, Cancellation);

        // The queued request runs on the new activation. Hold it until the retiring silo has departed,
        // so that the caller's target-silo failure sweep would complete the call if it had not been retargeted.
        var destination = await state.HoldEntered.Task.WaitAsync(Timeout, Cancellation);
        Assert.NotEqual(retiring.SiloAddress, destination);
        await stopping.WaitAsync(Timeout, Cancellation);
        await cluster.WaitForLivenessToStabilizeAsync().WaitAsync(Timeout, Cancellation);
        Assert.False(queued.IsCompleted, $"{queued.Status}: {queued.Exception?.InnerException}");

        state.ReleaseHold.TrySetResult();
        Assert.Equal(destination, await queued.WaitAsync(Timeout, Cancellation));
        Assert.Equal(1, state.HoldCount);
        Assert.Equal(destination, await grain.GetLocation().AsTask().WaitAsync(Timeout, Cancellation));
    }

    [Fact]
    public async Task QueuedRequestFromCallerOnRetiringSilo_CompletesBeforeCallerDeactivates()
    {
        var builder = new InProcessTestClusterBuilder(3);
        await using var cluster = builder.Build();
        await cluster.DeployAsync(Cancellation);
        await cluster.WaitForLivenessToStabilizeAsync().WaitAsync(Cancellation);
        await cluster.WaitForClusterManifestToStabilizeAsync().WaitAsync(Cancellation);

        var retiring = cluster.Silos[1];
        var grainFactory = cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>();
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var target = grainFactory.GetGrain<IRetirementHandoffGrain>(key, typeof(RetirementHandoffGrain).FullName);
        var sender = grainFactory.GetGrain<IRetirementHandoffCallerGrain>(Guid.NewGuid());

        // Host both the sender and the target on the retiring silo.
        RequestContext.Set(IPlacementDirector.PlacementHintKey, retiring.SiloAddress);
        try
        {
            Assert.Equal(retiring.SiloAddress, await target.GetLocation().AsTask().WaitAsync(Timeout, Cancellation));
            Assert.Equal(retiring.SiloAddress, await sender.GetLocation().AsTask().WaitAsync(Timeout, Cancellation));
        }
        finally
        {
            RequestContext.Remove(IPlacementDirector.PlacementHintKey);
        }

        var running = target.Block().AsTask();
        await state.BlockEntered.Task.WaitAsync(Timeout, Cancellation);
        var call = sender.CallHoldThenGetLocation(key).AsTask();
        await WaitUntilAsync(() => GetWaitingCount(retiring, target.GetGrainId()) == 1);

        var stopping = cluster.StopSiloAsync(retiring, Cancellation);
        await WaitUntilAsync(() => FindActivation(retiring, target.GetGrainId()) is ActivationData { State: ActivationState.Deactivating });
        state.ReleaseBlock.TrySetResult();
        await running.WaitAsync(Timeout, Cancellation);

        // The target migrated with the queued request. The sender is still running its request, which awaits the
        // migrated request, so the retiring silo keeps the sender and its pending call until the response arrives.
        var destination = await state.HoldEntered.Task.WaitAsync(Timeout, Cancellation);
        Assert.NotEqual(retiring.SiloAddress, destination);
        Assert.False(stopping.IsCompleted);
        Assert.False(call.IsCompleted);

        state.ReleaseHold.TrySetResult();
        Assert.Equal(destination, await call.WaitAsync(Timeout, Cancellation));
        await stopping.WaitAsync(Timeout, Cancellation);
        Assert.Equal(1, state.HoldCount);
    }

    [Fact]
    public async Task QueuedRequestForImmovableGrain_FailsWhenRetiringSiloDeparts()
    {
        var builder = new InProcessTestClusterBuilder(3);
        await using var cluster = builder.Build();
        await cluster.DeployAsync(Cancellation);
        await cluster.WaitForLivenessToStabilizeAsync().WaitAsync(Cancellation);
        await cluster.WaitForClusterManifestToStabilizeAsync().WaitAsync(Cancellation);

        var retiring = cluster.Silos[1];
        var caller = cluster.Silos[0];
        var grainFactory = caller.ServiceProvider.GetRequiredService<IGrainFactory>();
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grain = grainFactory.GetGrain<IImmovableRetirementHandoffGrain>(key);

        RequestContext.Set(IPlacementDirector.PlacementHintKey, retiring.SiloAddress);
        try
        {
            Assert.Equal(retiring.SiloAddress, await grain.GetLocation().AsTask().WaitAsync(Timeout, Cancellation));
        }
        finally
        {
            RequestContext.Remove(IPlacementDirector.PlacementHintKey);
        }

        var running = grain.Block().AsTask();
        await state.BlockEntered.Task.WaitAsync(Timeout, Cancellation);
        var queued = grain.HoldThenGetLocation().AsTask();
        await WaitUntilAsync(() => GetWaitingCount(retiring, grain.GetGrainId()) == 1);

        // Without migration, the queued request is forwarded but its caller is not retargeted.
        // When the retiring silo departs before the forwarded request completes, the call fails promptly instead of
        // waiting for its response timeout. The caller's application code decides whether to retry.
        var stopping = cluster.StopSiloAsync(retiring, Cancellation);
        await WaitUntilAsync(() => FindActivation(retiring, grain.GetGrainId()) is ActivationData { State: ActivationState.Deactivating });
        state.ReleaseBlock.TrySetResult();
        await running.WaitAsync(Timeout, Cancellation);
        await stopping.WaitAsync(Timeout, Cancellation);

        try
        {
            await Assert.ThrowsAsync<SiloUnavailableException>(() => queued.WaitAsync(Timeout, Cancellation));
        }
        finally
        {
            state.ReleaseHold.TrySetResult();
        }
    }

    [Fact]
    public async Task QueuedRequestFromCallerOnRetiringSilo_ReceivesCancellationAfterHandoff()
    {
        // Waiting for acknowledgement keeps the sender's request running until the canceled request responds,
        // so the retiring silo cannot depart, and cancel the request from its departed caller, before the signal arrives.
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) =>
            silo.Configure<SiloMessagingOptions>(options => options.WaitForCancellationAcknowledgement = true)));
        var retiring = cluster.Silos[1];
        var grainFactory = cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>();
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var target = grainFactory.GetGrain<IRetirementHandoffGrain>(key, typeof(RetirementHandoffGrain).FullName);
        var sender = grainFactory.GetGrain<IRetirementHandoffCallerGrain>(Guid.NewGuid());
        await PlaceOnAsync(retiring, () => target.GetLocation());
        await PlaceOnAsync(retiring, () => sender.GetLocation());

        var running = target.Block().AsTask();
        await state.BlockEntered.Task.WaitAsync(Timeout, Cancellation);
        var call = sender.CallHoldUntilCanceled(key).AsTask();
        await WaitUntilAsync(() => GetWaitingCount(retiring, target.GetGrainId()) == 1);

        var stopping = cluster.StopSiloAsync(retiring, Cancellation);
        await WaitUntilAsync(() => FindActivation(retiring, target.GetGrainId()) is ActivationData { State: ActivationState.Deactivating });
        state.ReleaseBlock.TrySetResult();
        await running.WaitAsync(Timeout, Cancellation);

        // The sender's pending call shares its request message with the retiring activation's queue.
        // Once the handoff completes, canceling the call signals the request on its new silo.
        Assert.NotEqual(retiring.SiloAddress, await state.HoldEntered.Task.WaitAsync(Timeout, Cancellation));
        await WaitUntilAsync(() => FindActivation(retiring, target.GetGrainId()) is null);
        Assert.False(stopping.IsCompleted);
        state.Cancellation.Cancel();
        await state.HoldCanceled.Task.WaitAsync(Timeout, Cancellation);
        Assert.True(await call.WaitAsync(Timeout, Cancellation));
        await stopping.WaitAsync(Timeout, Cancellation);
    }

    [Fact]
    public async Task QueuedRequestWithExhaustedForwards_IsRejectedInsteadOfHandedOff()
    {
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) =>
            silo.Configure<SiloMessagingOptions>(options => options.MaxForwardCount = 0)));
        var retiring = cluster.Silos[1];
        var grainFactory = cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>();
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grain = grainFactory.GetGrain<IRetirementHandoffGrain>(key, typeof(RetirementHandoffGrain).FullName);
        await PlaceOnAsync(retiring, () => grain.GetLocation());

        var running = grain.Block().AsTask();
        await state.BlockEntered.Task.WaitAsync(Timeout, Cancellation);
        var queued = grain.HoldThenGetLocation().AsTask();
        await WaitUntilAsync(() => GetWaitingCount(retiring, grain.GetGrainId()) == 1);

        // A handoff counts as a forward, so the request stays queued and is rejected when it cannot be forwarded.
        var stopping = cluster.StopSiloAsync(retiring, Cancellation);
        await WaitUntilAsync(() => FindActivation(retiring, grain.GetGrainId()) is ActivationData { State: ActivationState.Deactivating });
        state.ReleaseBlock.TrySetResult();
        await running.WaitAsync(Timeout, Cancellation);

        try
        {
            // The rejection and the retiring silo's departure race to the caller. Either way, the call fails promptly without running elsewhere.
            await Assert.ThrowsAnyAsync<OrleansMessageRejectionException>(() => queued.WaitAsync(Timeout, Cancellation));
            await stopping.WaitAsync(Timeout, Cancellation);
            Assert.Equal(0, state.HoldCount);
        }
        finally
        {
            state.ReleaseHold.TrySetResult();
        }
    }

    [Fact]
    public async Task MigratedRequestResponse_UpdatesTheCallersCacheToTheNewActivation()
    {
        await using var cluster = await DeployAsync();
        var caller = cluster.Silos[0];
        var source = cluster.Silos[1];
        var target = cluster.Silos[2];
        var grainFactory = caller.ServiceProvider.GetRequiredService<IGrainFactory>();
        var locator = caller.ServiceProvider.GetRequiredService<Orleans.Runtime.GrainDirectory.GrainLocator>();
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grain = grainFactory.GetGrain<IRetirementHandoffGrain>(key, typeof(RetirementHandoffGrain).FullName);
        await PlaceOnAsync(source, () => grain.GetLocation());
        Assert.True(locator.TryLookupInCache(grain.GetGrainId(), out var cached));
        Assert.Equal(source.SiloAddress, cached.SiloAddress);

        var running = grain.Block().AsTask();
        await state.BlockEntered.Task.WaitAsync(Timeout, Cancellation);
        var queued = grain.Record("queued").AsTask();
        await WaitUntilAsync(() => GetWaitingCount(source, grain.GetGrainId()) == 1);

        // The grain migrates with its queued request, and the source stays in the cluster.
        await grain.MigrateOnIdleTo(target.SiloAddress);
        state.ReleaseBlock.TrySetResult();
        await running.WaitAsync(Timeout, Cancellation);
        await queued.WaitAsync(Timeout, Cancellation);

        // The response to the migrated request replaces the old activation in the caller's cache, so its next call goes directly to the new activation.
        Assert.True(locator.TryLookupInCache(grain.GetGrainId(), out cached));
        Assert.Equal(target.SiloAddress, cached.SiloAddress);
    }

    [Fact]
    public async Task MigratedRequests_AreDeclinedWhenTheGrainIsAlreadyActive()
    {
        await using var cluster = await DeployAsync();
        var silo = cluster.Silos[1];
        var grainFactory = cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>();
        var migrationManager = silo.ServiceProvider.GetRequiredService<ActivationMigrationManager>();
        var key = Guid.NewGuid();
        RetirementHandoffState.Create(key);
        var grain = grainFactory.GetGrain<IRetirementHandoffGrain>(key, typeof(RetirementHandoffGrain).FullName);
        await PlaceOnAsync(silo, () => grain.GetLocation());
        var activation = Assert.IsType<ActivationData>(FindActivation(silo, grain.GetGrainId()));

        // The migration arrives too late to create the activation, so the existing activation does not take the requests, which stay with the source.
        GrainMigrationPackage package = new()
        {
            GrainId = grain.GetGrainId(),
            MigrationContext = new MigrationContext(silo.ServiceProvider.GetRequiredService<SerializerSessionPool>()),
            Requests = SerializeRequests(silo, [new() { Id = new CorrelationId(1), SendingGrain = GrainId.Create("test", "caller"), TimeToLiveMilliseconds = 60_000 }]),
        };
        var requestsAccepted = await migrationManager.AcceptMigratingGrainsWithRequests([package], Cancellation);
        Assert.False(Assert.Single(requestsAccepted));
        Assert.Equal(0, activation.WaitingCount);
        Assert.Same(activation, FindActivation(silo, grain.GetGrainId()));
    }

    [Fact]
    public async Task MigratedRequests_AreDeclinedWhenTheirInterfaceVersionIsIncompatible()
    {
        await using var cluster = await DeployAsync();
        var silo = cluster.Silos[1];
        var grainFactory = cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>();
        var migrationManager = silo.ServiceProvider.GetRequiredService<ActivationMigrationManager>();
        var key = Guid.NewGuid();
        RetirementHandoffState.Create(key);
        var grainId = grainFactory.GetGrain<IRetirementHandoffGrain>(key, typeof(RetirementHandoffGrain).FullName).GetGrainId();

        // The request targets a later version of the interface than this silo runs, which backward compatibility does not admit.
        // Its body decodes, so only the version check keeps this silo from decoding it with an earlier version's types.
        var request = MigratedRequest.FromMessage(new Message
        {
            Direction = Message.Directions.OneWay,
            Id = new CorrelationId(1),
            SendingGrain = GrainId.Create("test", "caller"),
            TargetGrain = grainId,
            InterfaceType = silo.ServiceProvider.GetRequiredService<Orleans.Metadata.GrainInterfaceTypeResolver>().GetGrainInterfaceType(typeof(IRetirementHandoffGrain)),
            InterfaceVersion = 5,
            BodyObject = new List<string> { "field" },
        }, silo.ServiceProvider.GetRequiredService<SerializerSessionPool>());
        GrainMigrationPackage package = new()
        {
            GrainId = grainId,
            MigrationContext = new MigrationContext(silo.ServiceProvider.GetRequiredService<SerializerSessionPool>()),
            Requests = SerializeRequests(silo, [request]),
        };

        // The new activation is created without the requests, which stay with the source, which forwards their original bytes.
        var requestsAccepted = await migrationManager.AcceptMigratingGrainsWithRequests([package], Cancellation);
        Assert.False(Assert.Single(requestsAccepted));
        var activation = Assert.IsType<ActivationData>(FindActivation(silo, grainId));
        Assert.Equal(0, activation.GetRequestCount());
    }

    [Fact]
    public async Task MigratedRequests_AreDeclinedWhenTheyWouldExceedTheHardLimit()
    {
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) =>
            silo.Configure<SiloMessagingOptions>(options => options.MaxEnqueuedRequestsHardLimit = 1)));
        var silo = cluster.Silos[1];
        var grainFactory = cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>();
        var migrationManager = silo.ServiceProvider.GetRequiredService<ActivationMigrationManager>();
        var key = Guid.NewGuid();
        RetirementHandoffState.Create(key);
        var grainId = grainFactory.GetGrain<IRetirementHandoffGrain>(key, typeof(RetirementHandoffGrain).FullName).GetGrainId();

        // Ordinary delivery admits two requests to an idle activation with a hard limit of one, so the new activation declines a batch of three rather than overloading.
        GrainMigrationPackage package = new()
        {
            GrainId = grainId,
            MigrationContext = new MigrationContext(silo.ServiceProvider.GetRequiredService<SerializerSessionPool>()),
            Requests = SerializeRequests(silo, [CreateMigratedRequest(1), CreateMigratedRequest(2), CreateMigratedRequest(3)]),
        };
        var requestsAccepted = await migrationManager.AcceptMigratingGrainsWithRequests([package], Cancellation);
        Assert.False(Assert.Single(requestsAccepted));
        var activation = Assert.IsType<ActivationData>(FindActivation(silo, grainId));
        Assert.Equal(0, activation.GetRequestCount());

        static MigratedRequest CreateMigratedRequest(long id) => new()
        {
            Id = new CorrelationId(id),
            SendingGrain = GrainId.Create("test", "caller"),
            TimeToLiveMilliseconds = 60_000,
        };
    }

    [Fact]
    public async Task MigratedRequests_AreDeclinedByAnActivationWhichHasLeftCreation()
    {
        await using var cluster = await DeployAsync();
        var silo = cluster.Silos[1];
        var grainFactory = cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>();
        var key = Guid.NewGuid();
        RetirementHandoffState.Create(key);
        var grain = grainFactory.GetGrain<IRetirementHandoffGrain>(key, typeof(RetirementHandoffGrain).FullName);
        await PlaceOnAsync(silo, () => grain.GetLocation());
        var activation = Assert.IsType<ActivationData>(FindActivation(silo, grain.GetGrainId()));

        // The catalog publishes a new activation before queuing its migrated requests, so another operation, such as retirement, can advance it first.
        // An activation which has left creation declines the requests without queuing any of them, so the source keeps and forwards them.
        using var request = new Message
        {
            Direction = Message.Directions.Request,
            Id = new CorrelationId(1),
            SendingGrain = GrainId.Create("test", "caller"),
            TargetGrain = grain.GetGrainId(),
        };
        Assert.False(activation.TryEnqueueMigratedRequests([request], previousAddress: null));
        Assert.Equal(0, activation.WaitingCount);
    }

    [Fact]
    public async Task MigratedRequests_RunBeforeRequestsWhichReachTheNewActivationDirectly()
    {
        await using var cluster = await DeployAsync();
        var source = cluster.Silos[1];
        var target = cluster.Silos[2];
        var grainFactory = cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>();
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grain = grainFactory.GetGrain<IRetirementHandoffGrain>(key, typeof(RetirementHandoffGrain).FullName);
        await PlaceOnAsync(source, () => grain.GetLocation());

        var running = grain.Block().AsTask();
        await state.BlockEntered.Task.WaitAsync(Timeout, Cancellation);
        var queued = grain.Record("queued").AsTask();
        await WaitUntilAsync(() => GetWaitingCount(source, grain.GetGrainId()) == 1);

        // The grain migrates with its queued request, and its new activation is held while activating, after it has registered in the grain directory.
        state.HoldActivationOn = target.SiloAddress;
        await grain.MigrateOnIdleTo(target.SiloAddress);
        state.ReleaseBlock.TrySetResult();
        await running.WaitAsync(Timeout, Cancellation);
        Task direct;
        try
        {
            await state.ActivationHeld.Task.WaitAsync(Timeout, Cancellation);

            // A call from the target's silo finds the new activation through the grain directory, so it reaches the new activation directly.
            direct = target.ServiceProvider.GetRequiredService<IGrainFactory>()
                .GetGrain<IRetirementHandoffGrain>(key, typeof(RetirementHandoffGrain).FullName).Record("direct").AsTask();
            await WaitUntilAsync(() => GetWaitingCount(target, grain.GetGrainId()) == 2);
        }
        finally
        {
            state.ReleaseActivation.TrySetResult();
        }

        // The migrated request was queued before the activation could receive any other request, so it runs first.
        await Task.WhenAll(queued, direct).WaitAsync(Timeout, Cancellation);
        Assert.Equal(["queued", "direct"], state.Calls);
    }

    [Fact]
    public async Task CancellationReachingRetiringActivation_FollowsHandedOffRequest()
    {
        // Hold callers' retargets, so that the caller cancels while its call still targets the retiring silo.
        var retargetArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRetarget = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationsDelivered = new ConcurrentQueue<SiloAddress>();
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) => silo.AddIncomingGrainCallFilter(async context =>
        {
            switch (context.Request.GetMethodName())
            {
                case nameof(IMigratingRequestsSystemTarget.RetargetMigratedRequests):
                    retargetArrived.TrySetResult();
                    await releaseRetarget.Task;
                    break;
                case nameof(IGrainCallCancellationManagerSystemTarget.CancelCallsAsync):
                    await context.Invoke();
                    cancellationsDelivered.Enqueue(context.TargetContext.Address.SiloAddress!);
                    return;
            }

            await context.Invoke();
        })));
        var retiring = cluster.Silos[1];
        var grainFactory = cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>();
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grain = grainFactory.GetGrain<IRetirementHandoffGrain>(key, typeof(RetirementHandoffGrain).FullName);
        await PlaceOnAsync(retiring, () => grain.GetLocation());

        var running = grain.Block().AsTask();
        await state.BlockEntered.Task.WaitAsync(Timeout, Cancellation);
        var call = grain.HoldUntilCanceled(state.Cancellation.Token).AsTask();
        await WaitUntilAsync(() => GetWaitingCount(retiring, grain.GetGrainId()) == 1);

        var stopping = cluster.StopSiloAsync(retiring, Cancellation);
        await WaitUntilAsync(() => FindActivation(retiring, grain.GetGrainId()) is ActivationData { State: ActivationState.Deactivating });
        state.ReleaseBlock.TrySetResult();
        await running.WaitAsync(Timeout, Cancellation);

        try
        {
            // The request runs on its new silo, while the retiring activation awaits its caller's retarget.
            Assert.NotEqual(retiring.SiloAddress, await state.HoldEntered.Task.WaitAsync(Timeout, Cancellation));
            await retargetArrived.Task.WaitAsync(Timeout, Cancellation);
            var activation = Assert.IsType<ActivationData>(FindActivation(retiring, grain.GetGrainId()));

            // The caller signals cancellation to the retiring activation, which no longer holds the request.
            // It keeps the cancellation until the migration completes, then passes it on to the request's new silo.
            state.Cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(Timeout, Cancellation));
            await WaitUntilAsync(() => cancellationsDelivered.Contains(retiring.SiloAddress));
            await activation.RunOrQueueTask(() => Task.CompletedTask);
            releaseRetarget.TrySetResult();
            await state.HoldCanceled.Task.WaitAsync(Timeout, Cancellation);
        }
        finally
        {
            releaseRetarget.TrySetResult();
            state.ReleaseHold.TrySetResult();
        }

        await stopping.WaitAsync(Timeout, Cancellation);
    }

    [Fact]
    public async Task MigrationRoutes_DoNotOverwriteALaterRetarget()
    {
        // Hold the destination's acknowledgement, so that a later route for the same call arrives while the migration awaits acceptance.
        var accepted = new TaskCompletionSource<MigratedRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAcceptance = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) => silo.AddIncomingGrainCallFilter(async context =>
        {
            await context.Invoke();
            if (context.Request.GetMethodName() == nameof(IMigratingRequestsSystemTarget.AcceptMigratingGrainsWithRequests))
            {
                var package = Assert.Single((List<GrainMigrationPackage>)context.Request.GetArgument(0)!);
                var requests = context.TargetContext.ActivationServices.GetRequiredService<Orleans.Serialization.Serializer>().Deserialize<List<MigratedRequest>>(package.Requests!)!;
                accepted.TrySetResult(Assert.Single(requests));
                await releaseAcceptance.Task;
            }
        })));
        var retiring = cluster.Silos[1];
        var grainFactory = cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>();
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var target = grainFactory.GetGrain<IRetirementHandoffGrain>(key, typeof(RetirementHandoffGrain).FullName);
        var sender = grainFactory.GetGrain<IRetirementHandoffCallerGrain>(Guid.NewGuid());
        await PlaceOnAsync(retiring, () => target.GetLocation());
        await PlaceOnAsync(retiring, () => sender.GetLocation());

        // The sender shares its request message with the target's queue, since both run on the retiring silo.
        var running = target.Block().AsTask();
        await state.BlockEntered.Task.WaitAsync(Timeout, Cancellation);
        var call = sender.CallHoldThenGetLocation(key).AsTask();
        await WaitUntilAsync(() => GetWaitingCount(retiring, target.GetGrainId()) == 1);

        var stopping = cluster.StopSiloAsync(retiring, Cancellation);
        await WaitUntilAsync(() => FindActivation(retiring, target.GetGrainId()) is ActivationData { State: ActivationState.Deactivating });
        state.ReleaseBlock.TrySetResult();
        await running.WaitAsync(Timeout, Cancellation);

        CallbackData callback;
        SiloAddress laterTarget;
        try
        {
            var request = await accepted.Task.WaitAsync(Timeout, Cancellation);
            var destination = await state.HoldEntered.Task.WaitAsync(Timeout, Cancellation);
            laterTarget = cluster.Silos.Select(silo => silo.SiloAddress).First(silo => !silo.Equals(retiring.SiloAddress) && !silo.Equals(destination));

            // A later migration of the same request, as by the destination retiring in turn, retargets the call first.
            var runtimeClient = retiring.ServiceProvider.GetRequiredService<InsideRuntimeClient>();
            callback = GetCallback(runtimeClient, request.SendingGrain, request.Id);
            runtimeClient.RetargetCallback(request.SendingGrain, request.Id, laterTarget, forwardCount: 2);
        }
        finally
        {
            releaseAcceptance.TrySetResult();
        }

        // The earlier migration's route is superseded, so the call keeps its later target.
        try
        {
            await WaitUntilAsync(() => FindActivation(retiring, target.GetGrainId()) is null);
            Assert.Equal(laterTarget, callback.Message.TargetSilo);
            Assert.Equal(2, callback.Message.ForwardCount);
        }
        finally
        {
            state.ReleaseHold.TrySetResult();
        }

        await call.WaitAsync(Timeout, Cancellation);
        await stopping.WaitAsync(Timeout, Cancellation);

        static CallbackData GetCallback(InsideRuntimeClient runtimeClient, GrainId sendingGrain, CorrelationId id)
        {
            var callbacks = (ConcurrentDictionary<(GrainId, CorrelationId), CallbackData>)typeof(InsideRuntimeClient)
                .GetField("callbacks", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(runtimeClient)!;
            return callbacks[(sendingGrain, id)];
        }
    }

    [Fact]
    public async Task RetiringSilo_DoesNotAwaitAbandonedStuckActivation()
    {
        var maxRequestProcessingTime = TimeSpan.FromSeconds(1);
        await using var cluster = await DeployAsync(builder =>
        {
            builder.ConfigureSilo((_, silo) => silo.Configure<SiloMessagingOptions>(options => options.MaxRequestProcessingTime = maxRequestProcessingTime));
            ExtendShutdownTimeouts(builder);
        });
        var retiring = cluster.Silos[1];
        var grainFactory = cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>();
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grain = grainFactory.GetGrain<IRetirementHandoffGrain>(key, typeof(RetirementHandoffGrain).FullName);
        await PlaceOnAsync(retiring, () => grain.GetLocation());

        var running = grain.Block().AsTask();
        await state.BlockEntered.Task.WaitAsync(Timeout, Cancellation);
        var stuck = Assert.IsType<ActivationData>(FindActivation(retiring, grain.GetGrainId()));

        // A request which arrives after the running request exceeds its processing limit causes the stuck activation to be abandoned.
        // The delay only needs to be a lower bound on the running request's duration.
        await Task.Delay(maxRequestProcessingTime * 2, Cancellation);
        await grain.GetLocation().AsTask().WaitAsync(Timeout, Cancellation);
        Assert.Equal(ActivationState.Deactivating, stuck.State);

        try
        {
            // The abandoned request keeps running, and the shutdown timeouts exceed the test's timeout, so shutdown completes only if it does not await the abandoned activation.
            await cluster.StopSiloAsync(retiring, Cancellation).WaitAsync(Timeout, Cancellation);
        }
        finally
        {
            state.ReleaseBlock.TrySetResult();
        }
    }

    [Fact]
    public async Task DeactivationUnderwayWhenRetiring_HandsOffQueuedRequests()
    {
        await using var cluster = await DeployAsync();
        var retiring = cluster.Silos[1];
        var grainFactory = cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>();
        var catalog = retiring.ServiceProvider.GetRequiredService<Catalog>();
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grain = grainFactory.GetGrain<IRetirementHandoffGrain>(key, typeof(RetirementHandoffGrain).FullName);
        await PlaceOnAsync(retiring, () => grain.GetLocation());

        var running = grain.Block().AsTask();
        await state.BlockEntered.Task.WaitAsync(Timeout, Cancellation);
        var queued = grain.HoldThenGetLocation().AsTask();
        await WaitUntilAsync(() => GetWaitingCount(retiring, grain.GetGrainId()) == 1);

        // The activation begins deactivating for another reason, and then its silo retires before the running request completes.
        var activation = Assert.IsType<ActivationData>(FindActivation(retiring, grain.GetGrainId()));
        activation.Deactivate(new DeactivationReason(DeactivationReasonCode.ApplicationRequested, "Test"), Cancellation);
        var stopping = cluster.StopSiloAsync(retiring, Cancellation);
        await WaitUntilAsync(() => catalog.IsRetiring);
        state.ReleaseBlock.TrySetResult();
        await running.WaitAsync(Timeout, Cancellation);

        try
        {
            // The queued request migrates with the activation, and its caller keeps waiting after the retiring silo departs.
            var destination = await state.HoldEntered.Task.WaitAsync(Timeout, Cancellation);
            Assert.NotEqual(retiring.SiloAddress, destination);
            await stopping.WaitAsync(Timeout, Cancellation);
            await cluster.WaitForLivenessToStabilizeAsync().WaitAsync(Timeout, Cancellation);
            Assert.False(queued.IsCompleted, $"{queued.Status}: {queued.Exception?.InnerException}");

            state.ReleaseHold.TrySetResult();
            Assert.Equal(destination, await queued.WaitAsync(Timeout, Cancellation));
        }
        finally
        {
            state.ReleaseHold.TrySetResult();
        }
    }

    [Fact]
    public async Task MigrationUnderwayWhenRetiring_CarriesQueuedRequests()
    {
        // Hold the destination's acceptance of the migrating activation, so that retirement begins while the migration is underway.
        var acceptArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAccept = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) => silo.AddIncomingGrainCallFilter(async context =>
        {
            if (context.Request.GetMethodName() == nameof(IMigratingRequestsSystemTarget.AcceptMigratingGrainsWithRequests))
            {
                acceptArrived.TrySetResult();
                await releaseAccept.Task;
            }

            await context.Invoke();
        })));
        var retiring = cluster.Silos[1];
        var target = cluster.Silos[2];
        var grainFactory = cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>();
        var catalog = retiring.ServiceProvider.GetRequiredService<Catalog>();
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grain = grainFactory.GetGrain<IRetirementHandoffGrain>(key, typeof(RetirementHandoffGrain).FullName);
        await PlaceOnAsync(retiring, () => grain.GetLocation());

        var running = grain.Block().AsTask();
        await state.BlockEntered.Task.WaitAsync(Timeout, Cancellation);
        var queued = grain.HoldThenGetLocation().AsTask();
        await WaitUntilAsync(() => GetWaitingCount(retiring, grain.GetGrainId()) == 1);

        // An ordinary migration begins once the running request completes, and waits for the destination to accept the activation.
        await grain.MigrateOnIdleTo(target.SiloAddress);
        state.ReleaseBlock.TrySetResult();
        await running.WaitAsync(Timeout, Cancellation);

        Task stopping;
        try
        {
            await acceptArrived.Task.WaitAsync(Timeout, Cancellation);
            stopping = cluster.StopSiloAsync(retiring, Cancellation);
            await WaitUntilAsync(() => catalog.IsRetiring);
        }
        finally
        {
            releaseAccept.TrySetResult();
        }

        try
        {
            // The queued request migrated with the activation, and its caller keeps waiting after the retiring silo departs.
            Assert.Equal(target.SiloAddress, await state.HoldEntered.Task.WaitAsync(Timeout, Cancellation));
            await stopping.WaitAsync(Timeout, Cancellation);
            await cluster.WaitForLivenessToStabilizeAsync().WaitAsync(Timeout, Cancellation);
            Assert.False(queued.IsCompleted, $"{queued.Status}: {queued.Exception?.InnerException}");

            state.ReleaseHold.TrySetResult();
            Assert.Equal(target.SiloAddress, await queued.WaitAsync(Timeout, Cancellation));
        }
        finally
        {
            state.ReleaseHold.TrySetResult();
        }
    }

    [Fact]
    public async Task DeactivationUnderwayWhenRetiring_KeepsActivationInCatalogAndIsAwaited()
    {
        // Without the outbound queue delay, the stage which deactivates all activations completes as soon as that deactivation does.
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) =>
            silo.Configure<SiloMessagingOptions>(options => options.WaitForMessageToBeQueuedForOutboundTime = TimeSpan.Zero)));
        var retiring = cluster.Silos[1];
        var grainFactory = cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>();
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grain = grainFactory.GetGrain<IDisposalHeldGrain>(key);
        await PlaceOnAsync(retiring, () => grain.GetLocation());
        var activation = Assert.IsType<ActivationData>(FindActivation(retiring, grain.GetGrainId()));
        var catalog = retiring.ServiceProvider.GetRequiredService<ActivationDirectory>();

        // The activation deactivates, and its disposal is held.
        await grain.DeactivateNow();
        await state.DisposeEntered.Task.WaitAsync(Timeout, Cancellation);

        // Until its deactivation completes, the activation stays in the catalog, so no other local activation of the grain is created.
        Assert.Same(activation, FindActivation(retiring, grain.GetGrainId()));

        using var stages = new SiloStageObserver(retiring.SiloAddress, ServiceLifecycleStage.GrainDeactivation, state.ReleaseDispose.Task);
        var stopping = cluster.StopSiloAsync(retiring, Cancellation);
        try
        {
            // Retirement deactivates the activations in the catalog, which includes the one whose deactivation is underway.
            await stages.Stopping.WaitAsync(Timeout, Cancellation);
            Assert.Same(activation, FindActivation(retiring, grain.GetGrainId()));

            // The stage cannot complete while the disposal is held, so this bounded wait never fails when retirement awaits the deactivation.
            // If retirement did not await it, the stage would complete within this window.
            var stoppedWhileHeld = await Task.WhenAny(stages.StoppedAfterRelease, Task.Delay(TimeSpan.FromSeconds(1), Cancellation));
            Assert.NotSame(stages.StoppedAfterRelease, stoppedWhileHeld);
        }
        finally
        {
            state.ReleaseDispose.TrySetResult();
        }

        // The stage which deactivates all activations (GrainDeactivation) completes only after the held disposal is released.
        Assert.True(await stages.StoppedAfterRelease.WaitAsync(Timeout, Cancellation));
        await stopping.WaitAsync(Timeout, Cancellation);
        Assert.Null(catalog.FindTarget(grain.GetGrainId()));
    }

    [Fact]
    public async Task CancellationWhileRequestIsInTransit_FollowsTheRequestOnceTheMigrationCompletes()
    {
        // Hold the migration before the destination creates the new activation, so that the caller cancels while its request is in transit.
        // The caller does not wait for acknowledgement, so it forgets the call as soon as it cancels.
        var acceptArrived = new TaskCompletionSource<SiloAddress>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAccept = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationsDelivered = new ConcurrentQueue<SiloAddress>();
        var grainId = default(GrainId);
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) => silo.AddIncomingGrainCallFilter(async context =>
        {
            switch (context.Request.GetMethodName())
            {
                case nameof(IMigratingRequestsSystemTarget.AcceptMigratingGrainsWithRequests):
                    acceptArrived.TrySetResult(context.TargetContext.Address.SiloAddress!);
                    await releaseAccept.Task;
                    break;
                case nameof(IGrainCallCancellationManagerSystemTarget.CancelCallsAsync):
                    await context.Invoke();
                    if (context.Request.GetArgument(0) is List<GrainCallCancellationRequest> requests && requests.Any(request => request.TargetGrainId == grainId))
                    {
                        cancellationsDelivered.Enqueue(context.TargetContext.Address.SiloAddress!);
                    }

                    return;
            }

            await context.Invoke();
        })));
        var retiring = cluster.Silos[1];
        var grainFactory = cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>();
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grain = grainFactory.GetGrain<IRetirementHandoffGrain>(key, typeof(RetirementHandoffGrain).FullName);
        grainId = grain.GetGrainId();
        await PlaceOnAsync(retiring, () => grain.GetLocation());

        var running = grain.Block().AsTask();
        await state.BlockEntered.Task.WaitAsync(Timeout, Cancellation);
        var call = grain.HoldUntilCanceled(state.Cancellation.Token).AsTask();
        await WaitUntilAsync(() => GetWaitingCount(retiring, grainId) == 1);

        var stopping = cluster.StopSiloAsync(retiring, Cancellation);
        await WaitUntilAsync(() => FindActivation(retiring, grainId) is ActivationData { State: ActivationState.Deactivating });
        state.ReleaseBlock.TrySetResult();
        await running.WaitAsync(Timeout, Cancellation);

        try
        {
            // The caller cancels the call, which still targets the retiring silo.
            var destination = await acceptArrived.Task.WaitAsync(Timeout, Cancellation);
            state.Cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(Timeout, Cancellation));
            await WaitUntilAsync(() => cancellationsDelivered.Contains(retiring.SiloAddress));

            // The activation which will hold the request does not exist yet, so the cancellation reaches the destination before the request does.
            // The retiring activation keeps the cancellation until the migration completes.
            await WaitUntilAsync(() => cancellationsDelivered.Contains(destination));

            // Once the migration completes, the cancellation follows the request again, and the request is canceled whether or not it has started running.
            releaseAccept.TrySetResult();
            await WaitUntilAsync(() => state.HoldCanceled.Task.IsCompleted
                || !state.HoldEntered.Task.IsCompleted && FindActivation(cluster.Silos.Single(silo => silo.SiloAddress.Equals(destination)), grainId) is ActivationData { State: ActivationState.Valid } target && target.GetRequestCount() == 0);
            await WaitUntilAsync(() => cancellationsDelivered.Count(silo => silo.Equals(destination)) >= 2);
        }
        finally
        {
            releaseAccept.TrySetResult();
            state.ReleaseHold.TrySetResult();
        }

        await stopping.WaitAsync(Timeout, Cancellation);
    }

    [Fact]
    public async Task CancellationWhileNewActivationIsActivating_CancelsTheMigratedRequestBeforeItRuns()
    {
        // The migration's outcome is known only once the new activation is valid. Extending the deactivation timeout ensures that the migration
        // does not end early while the new activation is held, so the request is canceled only if the cancellation reaches it during activation.
        await using var cluster = await DeployAsync(ExtendShutdownTimeouts);
        var source = cluster.Silos[1];
        var target = cluster.Silos[2];
        var grainFactory = cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>();
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grain = grainFactory.GetGrain<IRetirementHandoffGrain>(key, typeof(RetirementHandoffGrain).FullName);
        await PlaceOnAsync(source, () => grain.GetLocation());

        var running = grain.Block().AsTask();
        await state.BlockEntered.Task.WaitAsync(Timeout, Cancellation);
        var call = grain.HoldUntilCanceled(state.Cancellation.Token).AsTask();
        await WaitUntilAsync(() => GetWaitingCount(source, grain.GetGrainId()) == 1);

        // The grain migrates with its queued request, and its new activation is held while activating, with the request queued.
        state.HoldActivationOn = target.SiloAddress;
        await grain.MigrateOnIdleTo(target.SiloAddress);
        state.ReleaseBlock.TrySetResult();
        await running.WaitAsync(Timeout, Cancellation);
        try
        {
            await state.ActivationHeld.Task.WaitAsync(Timeout, Cancellation);
            var activation = Assert.IsType<ActivationData>(FindActivation(target, grain.GetGrainId()));
            Assert.Equal(1, activation.WaitingCount);

            // The caller's call still targets the source, which passes the cancellation on to the new activation, where the request is still queued.
            state.Cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(Timeout, Cancellation));
            await WaitUntilAsync(() => activation.WaitingCount == 0);

            // Once activated, the activation has no request to run.
            state.ReleaseActivation.TrySetResult();
            await WaitUntilAsync(() => activation.State is ActivationState.Valid);
            await activation.RunOrQueueTask(() => Task.CompletedTask);
            Assert.False(state.HoldEntered.Task.IsCompleted);
            Assert.Equal(0, activation.GetRequestCount());
        }
        finally
        {
            state.ReleaseActivation.TrySetResult();
            state.ReleaseHold.TrySetResult();
        }
    }

    [Fact]
    public async Task UnmatchedCancellation_IsNotForwardedByAnActivationWhoseRequestsDidNotMigrate()
    {
        // Observe the cancellations which reach the other silo.
        var delivered = new ConcurrentQueue<CorrelationId>();
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) => silo.AddIncomingGrainCallFilter(async context =>
        {
            await context.Invoke();
            if (context.Request.GetMethodName() == nameof(IGrainCallCancellationManagerSystemTarget.CancelCallsAsync)
                && context.Request.GetArgument(0) is List<GrainCallCancellationRequest> requests)
            {
                foreach (var request in requests)
                {
                    delivered.Enqueue(request.MessageId);
                }
            }
        })));
        var silo = cluster.Silos[1];
        var other = cluster.Silos[2];
        var grainFactory = cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>();
        var key = Guid.NewGuid();
        RetirementHandoffState.Create(key);
        var grain = grainFactory.GetGrain<IRetirementHandoffGrain>(key, typeof(RetirementHandoffGrain).FullName);
        await PlaceOnAsync(silo, () => grain.GetLocation());
        var activation = Assert.IsType<ActivationData>(FindActivation(silo, grain.GetGrainId()));

        // Like a duplicate activation, this activation has a forwarding address, but none of its requests migrated there.
        // Forwarding unmatched cancellations from such activations could cycle between them.
        activation.ForwardingAddress = other.SiloAddress;
        var sender = GrainId.Create("test", "caller");
        var unmatched = new CorrelationId(1);
        _ = ((IGrainCallCancellationExtension)activation).CancelRequestAsync(sender, unmatched, CancellationToken.None).AsTask();
        await activation.RunOrQueueTask(() => Task.CompletedTask);

        // Cancellations sent to one silo are delivered in order, so once a later cancellation arrives, an earlier forwarded one would have arrived too.
        var fence = new CorrelationId(2);
        silo.ServiceProvider.GetRequiredService<GrainCallCancellationManager>().SignalCancellation(other.SiloAddress, GrainId.Create("test", "fence"), sender, fence);
        await WaitUntilAsync(() => delivered.Contains(fence));
        Assert.DoesNotContain(unmatched, delivered);
    }

    [Fact]
    public async Task CancellationReachingMigratedActivation_FollowsItsRequestToTheNewActivation()
    {
        await using var cluster = await DeployAsync();
        var caller = cluster.Silos[0];
        var source = cluster.Silos[1];
        var target = cluster.Silos[2];
        var grainFactory = caller.ServiceProvider.GetRequiredService<IGrainFactory>();
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grain = grainFactory.GetGrain<IRetirementHandoffGrain>(key, typeof(RetirementHandoffGrain).FullName);
        await PlaceOnAsync(source, () => grain.GetLocation());
        var activation = Assert.IsType<ActivationData>(FindActivation(source, grain.GetGrainId()));

        var running = grain.Block().AsTask();
        await state.BlockEntered.Task.WaitAsync(Timeout, Cancellation);
        var call = grain.HoldUntilCanceled(CancellationToken.None).AsTask();
        await WaitUntilAsync(() => GetWaitingCount(source, grain.GetGrainId()) == 1);

        // The grain migrates with its queued request. The source activation's disposal is held, so it remains in the catalog after the migration completes.
        state.HoldDisposeOn = source.SiloAddress;
        await grain.MigrateOnIdleTo(target.SiloAddress);
        state.ReleaseBlock.TrySetResult();
        await running.WaitAsync(Timeout, Cancellation);
        var (sender, messageId) = FindPendingCall(caller, grain.GetGrainId());
        try
        {
            Assert.Equal(target.SiloAddress, await state.HoldEntered.Task.WaitAsync(Timeout, Cancellation));
            await state.DisposeEntered.Task.WaitAsync(Timeout, Cancellation);
            Assert.Same(activation, FindActivation(source, grain.GetGrainId()));

            // A cancellation which reaches the source activation follows the request to the new activation.
            await ((IGrainCallCancellationExtension)activation).CancelRequestAsync(sender, messageId, CancellationToken.None).AsTask().WaitAsync(Timeout, Cancellation);
            await state.HoldCanceled.Task.WaitAsync(Timeout, Cancellation);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(Timeout, Cancellation));
        }
        finally
        {
            state.ReleaseDispose.TrySetResult();
            state.ReleaseHold.TrySetResult();
        }

        static (GrainId Sender, CorrelationId Id) FindPendingCall(InProcessSiloHandle silo, GrainId target)
        {
            var callbacks = (ConcurrentDictionary<(GrainId, CorrelationId), CallbackData>)typeof(InsideRuntimeClient)
                .GetField("callbacks", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(silo.ServiceProvider.GetRequiredService<InsideRuntimeClient>())!;
            var pending = Assert.Single(callbacks, entry => entry.Value.Message.TargetGrain == target);
            return pending.Key;
        }
    }

    [Fact]
    public async Task QueuedOneWayRequest_MigratesWithoutRetargetingItsCaller()
    {
        var retargets = 0;
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) => silo.AddIncomingGrainCallFilter(async context =>
        {
            if (context.Request.GetMethodName() == nameof(IMigratingRequestsSystemTarget.RetargetMigratedRequests))
            {
                Interlocked.Increment(ref retargets);
            }

            await context.Invoke();
        })));
        var retiring = cluster.Silos[1];
        var grainFactory = cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>();
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grain = grainFactory.GetGrain<IRetirementHandoffGrain>(key, typeof(RetirementHandoffGrain).FullName);
        await PlaceOnAsync(retiring, () => grain.GetLocation());

        var running = grain.Block().AsTask();
        await state.BlockEntered.Task.WaitAsync(Timeout, Cancellation);
        await grain.RecordOneWay();
        await WaitUntilAsync(() => GetWaitingCount(retiring, grain.GetGrainId()) == 1);

        var stopping = cluster.StopSiloAsync(retiring, Cancellation);
        await WaitUntilAsync(() => FindActivation(retiring, grain.GetGrainId()) is ActivationData { State: ActivationState.Deactivating });
        state.ReleaseBlock.TrySetResult();
        await running.WaitAsync(Timeout, Cancellation);

        // The one-way request runs on the new silo, and its caller has no pending call to retarget.
        Assert.NotEqual(retiring.SiloAddress, await state.OneWayReceived.Task.WaitAsync(Timeout, Cancellation));
        await stopping.WaitAsync(Timeout, Cancellation);
        Assert.Equal(0, Volatile.Read(ref retargets));
    }

    [Fact]
    public async Task MigratedRequest_CarriesReceivedBodiesWithoutDecodingThem()
    {
        await using var cluster = await DeployAsync();
        var source = cluster.Silos[1];
        var destination = cluster.Silos[2];
        var pool = source.ServiceProvider.GetRequiredService<SerializerSessionPool>();

        // A body which this silo received, with a field which the destination might understand and this silo might not.
        var bodyBytes = SerializeBody(new List<string> { "first", "second" }, pool);
        using var bodyWriter = new Orleans.Serialization.Buffers.ArcBufferWriter();
        bodyWriter.Write(bodyBytes);
        var readRequest = new Orleans.Runtime.Messaging.MessageReadRequest(source.ServiceProvider.GetRequiredService<Orleans.Runtime.Messaging.MessageHandlerShared>());
        readRequest.Body = bodyWriter.ConsumeSlice(bodyBytes.Length);
        typeof(Orleans.Runtime.Messaging.MessageReadRequest).GetField("_bodyLength", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(readRequest, bodyBytes.Length);
        using var request = new Message
        {
            Direction = Message.Directions.Request,
            Id = new CorrelationId(1),
            SendingGrain = GrainId.Create("test", "caller"),
            TargetGrain = GrainId.Create("test", "target"),
        };
        request.SetMessageReadRequest(readRequest);

        // The migration copies the received bytes rather than decoding and re-encoding them, so no field is lost.
        var migrated = MigratedRequest.FromMessage(request, pool);
        Assert.Equal(bodyBytes, migrated.Body);
        Assert.Same(readRequest, request._bodyObject);

        // The destination decodes the body.
        var delivered = migrated.ToMessage(request.TargetGrain, destination.SiloAddress, destination.ServiceProvider.GetRequiredService<SerializerSessionPool>());
        Assert.Equal(["first", "second"], Assert.IsType<List<string>>(delivered.BodyObject));

        static byte[] SerializeBody(object body, SerializerSessionPool pool)
        {
            using var session = pool.GetSession();
            var writer = Orleans.Serialization.Buffers.Writer.CreatePooled(session);
            try
            {
                session.CodecProvider.GetCodec(body.GetType()).WriteField(ref writer, 0, null, body);
                writer.Commit();
                return writer.Output.ToArray();
            }
            finally
            {
                writer.Dispose();
            }
        }
    }

    [Fact]
    public async Task QueuedRequestsTooLargeToMigrateTogether_AreForwardedToTheMigratedActivation()
    {
        const int MaxMessageBodySize = 1024 * 1024;
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) =>
            silo.Configure<SiloMessagingOptions>(options => options.MaxMessageBodySize = MaxMessageBodySize)));
        var source = cluster.Silos[1];
        var target = cluster.Silos[2];
        var grainFactory = cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>();
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grain = grainFactory.GetGrain<IRetirementHandoffGrain>(key, typeof(RetirementHandoffGrain).FullName);
        await PlaceOnAsync(source, () => grain.GetLocation());

        // Each request fits in its own message, but not together with the other.
        var running = grain.Block().AsTask();
        await state.BlockEntered.Task.WaitAsync(Timeout, Cancellation);
        var first = grain.GetLocationWithPayload(new byte[MaxMessageBodySize * 3 / 5]).AsTask();
        var second = grain.GetLocationWithPayload(new byte[MaxMessageBodySize * 3 / 5]).AsTask();
        await WaitUntilAsync(() => GetWaitingCount(source, grain.GetGrainId()) == 2);

        // The grain migrates without the requests, which the source keeps queued and then forwards to the migrated activation.
        await grain.MigrateOnIdleTo(target.SiloAddress);
        state.ReleaseBlock.TrySetResult();
        await running.WaitAsync(Timeout, Cancellation);
        Assert.Equal(target.SiloAddress, await first.WaitAsync(Timeout, Cancellation));
        Assert.Equal(target.SiloAddress, await second.WaitAsync(Timeout, Cancellation));
    }

    [Fact]
    public async Task Migration_RetainsRequestsWhoseBodyCannotBeDeserialized()
    {
        await using var cluster = await DeployAsync();
        var source = cluster.Silos[1];
        var destination = cluster.Silos[2];
        var migrationManager = source.ServiceProvider.GetRequiredService<ActivationMigrationManager>();
        var grainFactory = cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>();
        var key = Guid.NewGuid();
        RetirementHandoffState.Create(key);
        var grainId = grainFactory.GetGrain<IRetirementHandoffGrain>(key, typeof(RetirementHandoffGrain).FullName).GetGrainId();

        // The body stands in for one carrying a type which neither silo understands.
        using var bodyWriter = new Orleans.Serialization.Buffers.ArcBufferWriter();
        bodyWriter.Write([0xff]);
        var readRequest = new Orleans.Runtime.Messaging.MessageReadRequest(source.ServiceProvider.GetRequiredService<Orleans.Runtime.Messaging.MessageHandlerShared>());
        readRequest.Body = bodyWriter.ConsumeSlice(1);
        typeof(Orleans.Runtime.Messaging.MessageReadRequest).GetField("_bodyLength", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(readRequest, 1);
        using var request = new Message
        {
            Direction = Message.Directions.Request,
            Id = new CorrelationId(1),
            SendingGrain = GrainId.Create("test", "caller"),
            SendingSilo = cluster.Silos[0].SiloAddress,
            TargetGrain = grainId,
        };
        request.SetMessageReadRequest(readRequest);

        // The grain migrates, but the destination cannot decode the body, so it declines the request.
        // The request stays with the source, whose ordinary forwarding sends the serialized body unchanged.
        var context = new MigrationContext(source.ServiceProvider.GetRequiredService<SerializerSessionPool>());
        var migration = migrationManager.MigrateAsync(destination.SiloAddress, grainId, context, [request], out var carriesRequests);
        Assert.True(carriesRequests);
        var result = await migration.AsTask().WaitAsync(Timeout, Cancellation);
        Assert.True(result.Migrated);
        Assert.Equal(MigratedRequestsOutcome.Retained, result.Requests);
        var activation = Assert.IsType<ActivationData>(FindActivation(destination, grainId));
        Assert.Equal(0, activation.WaitingCount);
        Assert.Same(readRequest, request._bodyObject);
        Assert.Equal([0xff], readRequest.Body.ToArray());
    }

    [Fact]
    public async Task CarryingRequests_LeavesTheMigrationContextIntact()
    {
        await using var cluster = await DeployAsync();
        var source = cluster.Silos[1];
        var target = cluster.Silos[2];
        var migrationManager = source.ServiceProvider.GetRequiredService<ActivationMigrationManager>();
        var grainFactory = cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>();
        var key = Guid.NewGuid();
        RetirementHandoffState.Create(key);
        var grain = grainFactory.GetGrain<IRetirementHandoffGrain>(key, typeof(RetirementHandoffGrain).FullName);

        // The grain is already active on the target, which therefore declines the requests without running them.
        await PlaceOnAsync(target, () => grain.GetLocation());
        // The value spans several of the context's buffer segments, which serializing the context would release.
        var marker = new string('x', 10_000);
        var context = new MigrationContext(source.ServiceProvider.GetRequiredService<SerializerSessionPool>());
        Assert.True(context.TryAddValue("marker", marker));
        using var request = new Message
        {
            Direction = Message.Directions.OneWay,
            Id = new CorrelationId(1),
            SendingGrain = GrainId.Create("test", "caller"),
            TargetGrain = grain.GetGrainId(),
            BodyObject = new byte[16],
        };

        // The migration manager's pump sends packages on the manager's scheduler, so packaging the requests there completes before the package is sent.
        // Sizing the package must not consume the migration context, which the target rehydrates the grain from.
        ValueTask<MigrationResult> migration = default;
        await migrationManager.RunOrQueueTask(() =>
        {
            migration = migrationManager.MigrateAsync(target.SiloAddress, grain.GetGrainId(), context, [request], out var carriesRequests);
            Assert.True(carriesRequests);
            Assert.True(context.TryGetValue("marker", out string? value));
            Assert.Equal(marker, value);
            return Task.CompletedTask;
        });

        var result = await migration.AsTask().WaitAsync(Timeout, Cancellation);
        Assert.True(result.Migrated);
        Assert.Equal(MigratedRequestsOutcome.Retained, result.Requests);
    }

    [Fact]
    public async Task Migration_RetainsRequestsWhichDoNotFitWithTheMigrationContext()
    {
        const int MaxMessageBodySize = 1024 * 1024;
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) =>
            silo.Configure<SiloMessagingOptions>(options => options.MaxMessageBodySize = MaxMessageBodySize)));
        var source = cluster.Silos[1];
        var target = cluster.Silos[2];
        var migrationManager = source.ServiceProvider.GetRequiredService<ActivationMigrationManager>();
        var grainFactory = cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>();
        var key = Guid.NewGuid();
        RetirementHandoffState.Create(key);
        var grainId = grainFactory.GetGrain<IRetirementHandoffGrain>(key, typeof(RetirementHandoffGrain).FullName).GetGrainId();

        // The request fits in a message by itself, and so does the migration context, whose size is mostly in its key, but not together.
        var context = new MigrationContext(source.ServiceProvider.GetRequiredService<SerializerSessionPool>());
        context.TryAddValue(new string('k', MaxMessageBodySize / 2), 0);
        using var request = new Message
        {
            Direction = Message.Directions.Request,
            Id = new CorrelationId(1),
            SendingGrain = GrainId.Create("test", "caller"),
            SendingSilo = cluster.Silos[0].SiloAddress,
            TargetGrain = grainId,
            BodyObject = new byte[MaxMessageBodySize * 3 / 5],
        };

        // The migration carries the context without the request, which the source retains.
        var migration = migrationManager.MigrateAsync(target.SiloAddress, grainId, context, [request], out var carriesRequests);
        Assert.False(carriesRequests);
        var result = await migration.AsTask().WaitAsync(Timeout, Cancellation);
        Assert.True(result.Migrated);
        Assert.Equal(MigratedRequestsOutcome.Retained, result.Requests);
    }

    [Fact]
    public async Task RequestWhichCannotBeCaptured_FailsAndTheOthersAreRetained()
    {
        await using var cluster = await DeployAsync();
        var source = cluster.Silos[1];
        var target = cluster.Silos[2];
        var migrationManager = source.ServiceProvider.GetRequiredService<ActivationMigrationManager>();
        var grainFactory = cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>();
        var key = Guid.NewGuid();
        RetirementHandoffState.Create(key);
        var grainId = grainFactory.GetGrain<IRetirementHandoffGrain>(key, typeof(RetirementHandoffGrain).FullName).GetGrainId();

        // The second request's context holds a pooled value, which serialization releases, followed by a value which cannot be serialized.
        var payload = Enumerable.Repeat((byte)0x5A, 20_000).ToArray();
        var retained = CreateRequest(1, new() { ["pooled"] = CreatePooledBuffer(payload) });
        var failed = CreateRequest(2, new() { ["pooled"] = CreatePooledBuffer(payload), ["unserializable"] = new UnserializableValue() });
        List<Message> requests = [retained, failed];

        // The migration carries neither request. The request which cannot be captured can no longer be delivered, so it fails rather than staying queued,
        // and the other request is restored for forwarding.
        var context = new MigrationContext(source.ServiceProvider.GetRequiredService<SerializerSessionPool>());
        var migration = migrationManager.MigrateAsync(target.SiloAddress, grainId, context, requests, out var carriesRequests);
        Assert.False(carriesRequests);
        Assert.Same(retained, Assert.Single(requests));
        Assert.Equal(payload, ((Orleans.Serialization.Buffers.PooledBuffer)retained.RequestContextData!["pooled"]).ToArray());

        var result = await migration.AsTask().WaitAsync(Timeout, Cancellation);
        Assert.True(result.Migrated);

        Message CreateRequest(long id, Dictionary<string, object> requestContext) => new()
        {
            Direction = Message.Directions.OneWay,
            Id = new CorrelationId(id),
            SendingGrain = GrainId.Create("test", "caller"),
            TargetGrain = grainId,
            RequestContextData = requestContext,
        };

        static Orleans.Serialization.Buffers.PooledBuffer CreatePooledBuffer(byte[] contents)
        {
            var buffer = new Orleans.Serialization.Buffers.PooledBuffer();
            buffer.Write(contents);
            return buffer;
        }
    }

    [Fact]
    public async Task ReceivedRequestWhichCannotBeCaptured_ReleasesItsBodyBuffer()
    {
        await using var cluster = await DeployAsync();
        var source = cluster.Silos[1];
        var target = cluster.Silos[2];
        var migrationManager = source.ServiceProvider.GetRequiredService<ActivationMigrationManager>();
        var grainFactory = cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>();
        var key = Guid.NewGuid();
        RetirementHandoffState.Create(key);
        var grainId = grainFactory.GetGrain<IRetirementHandoffGrain>(key, typeof(RetirementHandoffGrain).FullName).GetGrainId();

        // A request which this silo received holds its serialized body in a pooled read request. Its context cannot be serialized.
        using var bodyWriter = new Orleans.Serialization.Buffers.ArcBufferWriter();
        bodyWriter.Write([0x01]);
        var readRequest = new Orleans.Runtime.Messaging.MessageReadRequest(source.ServiceProvider.GetRequiredService<Orleans.Runtime.Messaging.MessageHandlerShared>());
        readRequest.Body = bodyWriter.ConsumeSlice(1);
        typeof(Orleans.Runtime.Messaging.MessageReadRequest).GetField("_bodyLength", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(readRequest, 1);
        var request = new Message
        {
            Direction = Message.Directions.Request,
            Id = new CorrelationId(1),
            SendingGrain = GrainId.Create("test", "caller"),
            SendingSilo = cluster.Silos[0].SiloAddress,
            TargetGrain = grainId,
            RequestContextData = new() { ["unserializable"] = new UnserializableValue() },
        };
        request.SetMessageReadRequest(readRequest);

        // The request fails, and its body buffer returns to its pool rather than remaining with the failed request.
        var context = new MigrationContext(source.ServiceProvider.GetRequiredService<SerializerSessionPool>());
        List<Message> requests = [request];
        var migration = migrationManager.MigrateAsync(target.SiloAddress, grainId, context, requests, out var carriesRequests);
        Assert.False(carriesRequests);
        Assert.Empty(requests);
        Assert.Null(request._bodyObject);
        await migration.AsTask().WaitAsync(Timeout, Cancellation);
    }

    private sealed class UnserializableValue;

    [Fact]
    public async Task Migration_RetainsRequestsWhichExceedTheMessageSizeLimitTogether()
    {
        const int MaxMessageBodySize = 1024 * 1024;
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) =>
            silo.Configure<SiloMessagingOptions>(options => options.MaxMessageBodySize = MaxMessageBodySize)));
        var source = cluster.Silos[1];
        var target = cluster.Silos[2];
        var migrationManager = source.ServiceProvider.GetRequiredService<ActivationMigrationManager>();
        var grainFactory = cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>();
        var key = Guid.NewGuid();
        RetirementHandoffState.Create(key);
        var grainId = grainFactory.GetGrain<IRetirementHandoffGrain>(key, typeof(RetirementHandoffGrain).FullName).GetGrainId();

        // Each request fits in its own message, but not together with the other, so the migration carries neither and the source retains them.
        // Their bodies are pooled buffers from callers on this silo, which capturing the requests for migration serializes, returning their storage to the pool.
        var payload = Enumerable.Repeat((byte)0x5A, MaxMessageBodySize * 3 / 5).ToArray();
        List<Message> requests = [CreateRequest(1), CreateRequest(2)];
        var context = new MigrationContext(source.ServiceProvider.GetRequiredService<SerializerSessionPool>());
        var migration = migrationManager.MigrateAsync(target.SiloAddress, grainId, context, requests, out var carriesRequests);
        Assert.False(carriesRequests);

        // The retained requests are restored, so that forwarding them sends their original bodies.
        Assert.All(requests, request => Assert.Equal(payload, ((Orleans.Serialization.Buffers.PooledBuffer)request.BodyObject!).ToArray()));

        // The grain migrates without them.
        var result = await migration.AsTask().WaitAsync(Timeout, Cancellation);
        Assert.True(result.Migrated);
        Assert.Equal(MigratedRequestsOutcome.Retained, result.Requests);

        Message CreateRequest(long id)
        {
            var body = new Orleans.Serialization.Buffers.PooledBuffer();
            body.Write(payload);
            return new()
            {
                Direction = Message.Directions.Request,
                Id = new CorrelationId(id),
                SendingGrain = GrainId.Create("test", "caller"),
                SendingSilo = cluster.Silos[0].SiloAddress,
                TargetGrain = grainId,
                BodyObject = body,
            };
        }
    }

    /// <summary>
    /// Raises the deactivation and host shutdown timeouts above the test's timeout, so that work which waits for them is observed as never completing.
    /// </summary>
    private static void ExtendShutdownTimeouts(InProcessTestClusterBuilder builder)
    {
        var timeout = TimeSpan.FromMinutes(10);
        builder.ConfigureSilo((_, silo) => silo.Configure<GrainCollectionOptions>(options => options.DeactivationTimeout = timeout));
        builder.ConfigureSiloHost((_, host) => host.Services.PostConfigure<Microsoft.Extensions.Hosting.HostOptions>(options => options.ShutdownTimeout = timeout));
    }

    private static async Task<InProcessTestCluster> DeployAsync(Action<InProcessTestClusterBuilder>? configure = null)
    {
        var builder = new InProcessTestClusterBuilder(3);
        configure?.Invoke(builder);
        var cluster = builder.Build();
        await cluster.DeployAsync(Cancellation);
        await cluster.WaitForLivenessToStabilizeAsync().WaitAsync(Cancellation);
        await cluster.WaitForClusterManifestToStabilizeAsync().WaitAsync(Cancellation);
        return cluster;
    }

    private static async Task PlaceOnAsync(InProcessSiloHandle silo, Func<ValueTask<SiloAddress>> getLocation)
    {
        RequestContext.Set(IPlacementDirector.PlacementHintKey, silo.SiloAddress);
        try
        {
            Assert.Equal(silo.SiloAddress, await getLocation().AsTask().WaitAsync(Timeout, Cancellation));
        }
        finally
        {
            RequestContext.Remove(IPlacementDirector.PlacementHintKey);
        }
    }

    private static Guid SelectKey(IGrainFactory grainFactory, InProcessSiloHandle observer, InProcessSiloHandle retiring, bool directoryOwnedByRetiringSilo)
    {
        var directory = observer.ServiceProvider.GetRequiredService<Orleans.Runtime.GrainDirectory.LocalGrainDirectory>();
        while (true)
        {
            var key = Guid.NewGuid();
            var grainId = grainFactory.GetGrain<IRetirementHandoffGrain>(key, typeof(RetirementHandoffGrain).FullName).GetGrainId();
            if (retiring.SiloAddress.Equals(directory.CalculateGrainDirectoryPartition(grainId)) == directoryOwnedByRetiringSilo)
            {
                return key;
            }
        }
    }

    private static byte[] SerializeRequests(InProcessSiloHandle silo, List<MigratedRequest> requests)
        => silo.ServiceProvider.GetRequiredService<Orleans.Serialization.Serializer>().SerializeToArray(requests);

    private static IGrainContext? FindActivation(InProcessSiloHandle silo, GrainId grainId)
        => silo.ServiceProvider.GetRequiredService<ActivationDirectory>().FindTarget(grainId);

    private static int GetWaitingCount(InProcessSiloHandle silo, GrainId grainId)
        => FindActivation(silo, grainId) is ActivationData activation ? activation.WaitingCount : 0;

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeLimit = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        timeout.CancelAfter(timeLimit ?? Timeout);
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}

public interface IRetirementHandoffGrain : IGrainWithGuidKey
{
    ValueTask<SiloAddress> GetLocation();

    ValueTask Block();

    ValueTask<SiloAddress> HoldThenGetLocation();

    ValueTask HoldUntilCanceled(CancellationToken cancellationToken);

    [OneWay]
    Task RecordOneWay();

    [AlwaysInterleave]
    ValueTask MigrateOnIdleTo(SiloAddress target);

    ValueTask Record(string call);

    ValueTask<SiloAddress> GetLocationWithPayload(byte[] payload);
}

public interface IDisposalHeldGrain : IGrainWithGuidKey
{
    ValueTask<SiloAddress> GetLocation();

    ValueTask DeactivateNow();
}

[PreferLocalPlacement]
public sealed class DisposalHeldGrain : Grain, IDisposalHeldGrain, IAsyncDisposable
{
    public ValueTask<SiloAddress> GetLocation() => new(GrainContext.Address.SiloAddress!);

    public ValueTask DeactivateNow()
    {
        DeactivateOnIdle();
        return default;
    }

    public async ValueTask DisposeAsync()
    {
        var state = RetirementHandoffState.Get(this.GetPrimaryKey());
        state.DisposeEntered.TrySetResult();
        await state.ReleaseDispose.Task;
    }
}

/// <summary>
/// Observes when one silo starts and finishes stopping one lifecycle stage.
/// </summary>
internal sealed class SiloStageObserver : IObserver<Orleans.Runtime.Diagnostics.SiloLifecycleEvents.LifecycleEvent>, IDisposable
{
    private readonly SiloAddress _silo;
    private readonly int _stage;
    private readonly Task _released;
    private readonly IDisposable _subscription;
    private readonly TaskCompletionSource _stopping = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SiloStageObserver(SiloAddress silo, int stage, Task released)
    {
        _silo = silo;
        _stage = stage;
        _released = released;
        _subscription = Orleans.Runtime.Diagnostics.SiloLifecycleEvents.AllEvents.Subscribe(this);
    }

    /// <summary>
    /// Completes when the first observer of the stage starts stopping.
    /// </summary>
    public Task Stopping => _stopping.Task;

    /// <summary>
    /// Completes when the stage has stopped, indicating whether the release task had completed by then.
    /// </summary>
    public Task<bool> StoppedAfterRelease => _stopped.Task;

    public void OnNext(Orleans.Runtime.Diagnostics.SiloLifecycleEvents.LifecycleEvent value)
    {
        if (value.Stage != _stage || !_silo.Equals(value.SiloAddress))
        {
            return;
        }

        switch (value)
        {
            case Orleans.Runtime.Diagnostics.SiloLifecycleEvents.ObserverStopping:
                _stopping.TrySetResult();
                break;
            case Orleans.Runtime.Diagnostics.SiloLifecycleEvents.StageStopped:
                _stopped.TrySetResult(_released.IsCompleted);
                break;
        }
    }

    public void OnCompleted() { }

    public void OnError(Exception error) { }

    public void Dispose() => _subscription.Dispose();
}

public interface IImmovableRetirementHandoffGrain : IRetirementHandoffGrain
{
}

public interface IRetirementHandoffCallerGrain : IGrainWithGuidKey
{
    ValueTask<SiloAddress> GetLocation();

    ValueTask<SiloAddress> CallHoldThenGetLocation(Guid targetKey);

    ValueTask<bool> CallHoldUntilCanceled(Guid targetKey);
}

[PreferLocalPlacement]
public sealed class RetirementHandoffCallerGrain : Grain, IRetirementHandoffCallerGrain
{
    public ValueTask<SiloAddress> GetLocation() => new(GrainContext.Address.SiloAddress!);

    public ValueTask<SiloAddress> CallHoldThenGetLocation(Guid targetKey)
        => GrainFactory.GetGrain<IRetirementHandoffGrain>(targetKey, typeof(RetirementHandoffGrain).FullName).HoldThenGetLocation();

    public async ValueTask<bool> CallHoldUntilCanceled(Guid targetKey)
    {
        var target = GrainFactory.GetGrain<IRetirementHandoffGrain>(targetKey, typeof(RetirementHandoffGrain).FullName);
        try
        {
            await target.HoldUntilCanceled(RetirementHandoffState.Get(targetKey).Cancellation.Token);
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }
}

[PreferLocalPlacement]
public class RetirementHandoffGrain : Grain, IRetirementHandoffGrain, IAsyncDisposable
{
    private RetirementHandoffState State => RetirementHandoffState.Get(this.GetPrimaryKey());

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        if (State.HoldActivationOn is { } silo && silo.Equals(GrainContext.Address.SiloAddress))
        {
            State.ActivationHeld.TrySetResult();
            await State.ReleaseActivation.Task;
        }
    }

    public ValueTask<SiloAddress> GetLocation() => new(GrainContext.Address.SiloAddress!);

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        if (State.HoldDisposeOn is { } silo && silo.Equals(GrainContext.Address.SiloAddress))
        {
            // Hold the deactivation's final steps, after migration, through the grain's disposal.
            _holdDisposal = true;
        }

        await base.OnDeactivateAsync(reason, cancellationToken);
    }

    private bool _holdDisposal;

    public async ValueTask DisposeAsync()
    {
        if (_holdDisposal)
        {
            State.DisposeEntered.TrySetResult();
            await State.ReleaseDispose.Task;
        }
    }

    public ValueTask<SiloAddress> GetLocationWithPayload(byte[] payload) => new(GrainContext.Address.SiloAddress!);

    public ValueTask Record(string call)
    {
        State.Calls.Enqueue(call);
        return default;
    }

    public async ValueTask Block()
    {
        State.BlockEntered.TrySetResult();
        await State.ReleaseBlock.Task;
    }

    public async ValueTask<SiloAddress> HoldThenGetLocation()
    {
        var location = GrainContext.Address.SiloAddress!;
        Interlocked.Increment(ref State.HoldCount);
        State.HoldEntered.TrySetResult(location);
        await State.ReleaseHold.Task;
        return location;
    }

    public ValueTask MigrateOnIdleTo(SiloAddress target)
    {
        RequestContext.Set(IPlacementDirector.PlacementHintKey, target);
        MigrateOnIdle();
        return default;
    }

    public Task RecordOneWay()
    {
        State.OneWayReceived.TrySetResult(GrainContext.Address.SiloAddress!);
        return Task.CompletedTask;
    }

    public async ValueTask HoldUntilCanceled(CancellationToken cancellationToken)
    {
        State.HoldEntered.TrySetResult(GrainContext.Address.SiloAddress!);
        try
        {
            // The test releases a request which was never canceled, so that a failing test does not hold up the silo's shutdown.
            await State.ReleaseHold.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            State.HoldCanceled.TrySetResult();
            throw;
        }
    }
}

[PreferLocalPlacement, Immovable]
public sealed class ImmovableRetirementHandoffGrain : RetirementHandoffGrain, IImmovableRetirementHandoffGrain
{
}

public sealed class RetirementHandoffState
{
    private static readonly ConcurrentDictionary<Guid, RetirementHandoffState> States = new();

    public TaskCompletionSource BlockEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseBlock { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<SiloAddress> HoldEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseHold { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource HoldCanceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource DisposeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseDispose { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public CancellationTokenSource Cancellation { get; } = new();
    public TaskCompletionSource<SiloAddress> OneWayReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ActivationHeld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseActivation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ConcurrentQueue<string> Calls { get; } = new();
    public SiloAddress? HoldActivationOn { get; set; }
    public SiloAddress? HoldDisposeOn { get; set; }
    public int HoldCount;

    public static RetirementHandoffState Create(Guid key) => States[key] = new();

    public static RetirementHandoffState Get(Guid key) => States[key];
}
