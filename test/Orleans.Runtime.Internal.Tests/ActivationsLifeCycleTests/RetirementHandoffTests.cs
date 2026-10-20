using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Concurrency;
using Orleans.Configuration;
using Orleans.Metadata;
using Orleans.Placement;
using Orleans.Placement.Repartitioning;
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
    public async Task MigratabilityChecker_ResolvesConstructedGenericGrainsByTheirClass()
    {
        await using var cluster = await DeployAsync();
        var services = cluster.Silos[1].ServiceProvider;
        var checker = services.GetRequiredService<GrainMigratabilityChecker>();
        var grainFactory = services.GetRequiredService<IGrainFactory>();

        // Constructed generic grains are described by their unconstructed class, which is migratable here.
        Assert.True(checker.IsMigratable(grainFactory.GetGrain<IGenericRetirementHandoffGrain<int>>(Guid.NewGuid()).GetGrainId().Type, Orleans.Placement.ImmovableKind.Any));
        Assert.True(checker.IsMigratable(grainFactory.GetGrain<IGenericRetirementHandoffGrain<string>>(Guid.NewGuid()).GetGrainId().Type, Orleans.Placement.ImmovableKind.Any));

        // Immovable and stateless worker grains, and grain types which this silo does not know, remain unmigratable.
        Assert.True(checker.IsMigratable(grainFactory.GetGrain<IRetirementHandoffGrain>(Guid.NewGuid(), typeof(RetirementHandoffGrain).FullName).GetGrainId().Type, Orleans.Placement.ImmovableKind.Any));
        Assert.False(checker.IsMigratable(grainFactory.GetGrain<IImmovableRetirementHandoffGrain>(Guid.NewGuid()).GetGrainId().Type, Orleans.Placement.ImmovableKind.Any));
        Assert.False(checker.IsMigratable(grainFactory.GetGrain<IStatelessRetirementHandoffGrain>(Guid.NewGuid()).GetGrainId().Type, Orleans.Placement.ImmovableKind.Any));
        Assert.False(checker.IsMigratable(GrainType.Create("unknown-grain-type"), Orleans.Placement.ImmovableKind.Any));
    }

    [Fact]
    public async Task QueuedRequestForGenericGrain_MigratesWithActivation_AndSurvivesRetiringSiloDeparture()
    {
        await using var cluster = await DeployAsync();
        var retiring = cluster.Silos[1];
        var grainFactory = cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>();
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grain = grainFactory.GetGrain<IGenericRetirementHandoffGrain<int>>(key);
        await PlaceOnAsync(retiring, () => grain.GetLocation());

        // A constructed generic grain is migratable unless its class is immovable.
        Assert.True(retiring.ServiceProvider.GetRequiredService<GrainMigratabilityChecker>().IsMigratable(grain.GetGrainId().Type, Orleans.Placement.ImmovableKind.Any));

        var running = grain.Block().AsTask();
        await state.BlockEntered.Task.WaitAsync(Timeout, Cancellation);
        var queued = grain.HoldThenGetLocation().AsTask();
        await WaitUntilAsync(() => GetWaitingCount(retiring, grain.GetGrainId()) == 1);

        var stopping = cluster.StopSiloAsync(retiring, Cancellation);
        await WaitUntilAsync(() => FindActivation(retiring, grain.GetGrainId()) is ActivationData { State: ActivationState.Deactivating });
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
    public async Task RequestMigratedTwice_KeepsItsFirstCacheUpdate()
    {
        await using var cluster = await DeployAsync();
        var caller = cluster.Silos[0];
        var first = cluster.Silos[1];
        var second = cluster.Silos[2];
        var grainFactory = caller.ServiceProvider.GetRequiredService<IGrainFactory>();
        var locator = caller.ServiceProvider.GetRequiredService<Orleans.Runtime.GrainDirectory.GrainLocator>();
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grain = grainFactory.GetGrain<IRetirementHandoffGrain>(key, typeof(RetirementHandoffGrain).FullName);
        await PlaceOnAsync(first, () => grain.GetLocation());

        // Two requests queue behind a running request on the first activation.
        var running = grain.Block().AsTask();
        await state.BlockEntered.Task.WaitAsync(Timeout, Cancellation);
        var hold = grain.HoldThenGetLocation().AsTask();
        await WaitUntilAsync(() => GetWaitingCount(first, grain.GetGrainId()) == 1);
        var queued = grain.Record("queued").AsTask();
        await WaitUntilAsync(() => GetWaitingCount(first, grain.GetGrainId()) == 2);

        // Both migrate to the second silo, where the first runs while the other waits.
        await grain.MigrateOnIdleTo(second.SiloAddress);
        state.ReleaseBlock.TrySetResult();
        await running.WaitAsync(Timeout, Cancellation);
        Assert.Equal(second.SiloAddress, await state.HoldEntered.Task.WaitAsync(Timeout, Cancellation));
        await WaitUntilAsync(() => GetWaitingCount(second, grain.GetGrainId()) == 1);

        // The waiting request migrates again, back to the first silo, where the third activation is held while activating,
        // so that its response follows the response from the second activation.
        state.HoldActivationOn = first.SiloAddress;
        await grain.MigrateOnIdleTo(first.SiloAddress);
        state.ReleaseHold.TrySetResult();
        Assert.Equal(second.SiloAddress, await hold.WaitAsync(Timeout, Cancellation));
        Assert.True(locator.TryLookupInCache(grain.GetGrainId(), out var cached));
        Assert.Equal(second.SiloAddress, cached.SiloAddress);

        // Only the response's cache update can repopulate the caller's cache.
        locator.InvalidateCache(grain.GetGrainId());
        try
        {
            await state.ActivationHeld.Task.WaitAsync(Timeout, Cancellation);
        }
        finally
        {
            state.ReleaseActivation.TrySetResult();
        }

        // A message holds one cache update per grain, its first, so the response carries the update from the grain's original activation to its second.
        await queued.WaitAsync(Timeout, Cancellation);
        Assert.True(locator.TryLookupInCache(grain.GetGrainId(), out cached));
        Assert.Equal(second.SiloAddress, cached.SiloAddress);
    }

    [Fact]
    public async Task MigrationWhoseAcknowledgementIsLost_RejectsItsRequestsWithoutResendingThem()
    {
        // The target accepts the migration and its requests, but the source does not receive the acknowledgement.
        var retargets = 0;
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) => silo.AddIncomingGrainCallFilter(async context =>
        {
            switch (context.Request.GetMethodName())
            {
                case nameof(IActivationMigrationManagerSystemTarget.AcceptMigratingGrains):
                    await context.Invoke();
                    throw new InvalidOperationException("The acknowledgement is lost.");
                case nameof(IActivationMigrationManagerSystemTarget.RetargetMigratedRequests):
                    Interlocked.Increment(ref retargets);
                    break;
            }

            await context.Invoke();
        })));
        var source = cluster.Silos[1];
        var target = cluster.Silos[2];
        var grainFactory = cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>();
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grain = grainFactory.GetGrain<IRetirementHandoffGrain>(key, typeof(RetirementHandoffGrain).FullName);
        await PlaceOnAsync(source, () => grain.GetLocation());
        var activation = Assert.IsType<ActivationData>(FindActivation(source, grain.GetGrainId()));

        var running = grain.Block().AsTask();
        await state.BlockEntered.Task.WaitAsync(Timeout, Cancellation);
        var queued = grain.HoldThenGetLocation().AsTask();
        await WaitUntilAsync(() => GetWaitingCount(source, grain.GetGrainId()) == 1);

        await grain.MigrateOnIdleTo(target.SiloAddress);
        state.ReleaseBlock.TrySetResult();
        await running.WaitAsync(Timeout, Cancellation);

        // The target may own the requests, so the source rejects them rather than resending them, and finishes deactivating.
        await activation.Deactivated.WaitAsync(Timeout, Cancellation);
        Assert.Equal(0, activation.GetRequestCount());

        try
        {
            // The target has begun execution, but cannot respond yet, so the source's rejection must complete the call.
            Assert.Equal(target.SiloAddress, await state.HoldEntered.Task.WaitAsync(Timeout, Cancellation));
            await Assert.ThrowsAsync<OrleansMessageRejectionException>(() => queued.WaitAsync(Timeout, Cancellation));
        }
        finally
        {
            state.ReleaseHold.TrySetResult();
        }

        // A request which the source forwarded would travel to the target ahead of a later call from the source, and would run first.
        var fromSource = source.ServiceProvider.GetRequiredService<IGrainFactory>().GetGrain<IRetirementHandoffGrain>(key, typeof(RetirementHandoffGrain).FullName);
        Assert.Equal(target.SiloAddress, await fromSource.GetLocation().AsTask().WaitAsync(Timeout, Cancellation));
        Assert.Equal(1, state.HoldCount);
        Assert.Equal(0, Volatile.Read(ref retargets));
    }

    [Fact]
    public async Task MigrationOutcome_IsAwaitedPastTheDeactivationTimeout()
    {
        // The target holds the migration past the source activation's deactivation timeout, and then fails it.
        var acceptArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) =>
        {
            silo.Configure<GrainCollectionOptions>(options => options.DeactivationTimeout = DeactivationTimeout);
            silo.AddIncomingGrainCallFilter(async context =>
            {
                if (context.Request.GetMethodName() == nameof(IActivationMigrationManagerSystemTarget.AcceptMigratingGrains))
                {
                    acceptArrived.TrySetResult();
                    await releaseFailure.Task;
                    throw new InvalidOperationException("The migration fails.");
                }

                await context.Invoke();
            });
        }));
        var source = cluster.Silos[1];
        var target = cluster.Silos[2];
        var grainFactory = cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>();
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grain = grainFactory.GetGrain<IRetirementHandoffGrain>(key, typeof(RetirementHandoffGrain).FullName);
        await PlaceOnAsync(source, () => grain.GetLocation());
        var activation = Assert.IsType<ActivationData>(FindActivation(source, grain.GetGrainId()));

        var running = grain.Block().AsTask();
        await state.BlockEntered.Task.WaitAsync(Timeout, Cancellation);
        var queued = grain.HoldThenGetLocation().AsTask();
        await WaitUntilAsync(() => GetWaitingCount(source, grain.GetGrainId()) == 1);

        await grain.MigrateOnIdleTo(target.SiloAddress);
        state.ReleaseBlock.TrySetResult();
        await running.WaitAsync(Timeout, Cancellation);

        try
        {
            // The target may take the requests until it responds, so the source activation remains in the catalog past its deactivation timeout,
            // and no other activation of the grain starts.
            await acceptArrived.Task.WaitAsync(Timeout, Cancellation);
            await Task.Delay(DeactivationTimeout * 2, Cancellation);
            Assert.False(activation.Deactivated.IsCompleted);
            Assert.Same(activation, FindActivation(source, grain.GetGrainId()));
            Assert.Null(FindActivation(target, grain.GetGrainId()));
            Assert.False(state.HoldEntered.Task.IsCompleted);
        }
        finally
        {
            releaseFailure.TrySetResult();
        }

        // Once the migration fails, the source rejects the request, which did not run, so its caller can retry it.
        await activation.Deactivated.WaitAsync(Timeout, Cancellation);
        await Assert.ThrowsAsync<OrleansMessageRejectionException>(() => queued.WaitAsync(Timeout, Cancellation));
        Assert.Equal(0, state.HoldCount);
    }

    [Fact]
    public async Task MigrationCanceledBeforeItIsSent_IsNotSentAndForwardsItsRequests()
    {
        var migrationsReceived = 0;
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) => silo.AddIncomingGrainCallFilter(async context =>
        {
            if (context.Request.GetMethodName() == nameof(IActivationMigrationManagerSystemTarget.AcceptMigratingGrains))
            {
                Interlocked.Increment(ref migrationsReceived);
            }

            await context.Invoke();
        })));
        var source = cluster.Silos[1];
        var target = cluster.Silos[2];
        var migrationManager = source.ServiceProvider.GetRequiredService<ActivationMigrationManager>();
        var grainId = cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>().GetGrain<IRetirementHandoffGrain>(Guid.NewGuid(), typeof(RetirementHandoffGrain).FullName).GetGrainId();
        var original = CreateRequest(cluster, grainId, 1, CreatePooledBuffer(Enumerable.Repeat((byte)0x5A, 20_000).ToArray()));
        List<Message> requests = [original];

        // The activation stops waiting before the manager sends the migration, so the manager does not send it.
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var migration = migrationManager.StartMigration(target.SiloAddress, CreateSourceAddress(source, grainId), new MigrationContext(source.ServiceProvider.GetRequiredService<SerializerSessionPool>()), requests, canceled.Token);
        Assert.NotNull(migration.Package.Requests);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => migration.Completion.WaitAsync(Timeout, Cancellation));
        Assert.Equal(0, Volatile.Read(ref migrationsReceived));

        // The requests remain with the migration until the activation stops receiving requests, and are then forwarded, unchanged.
        migration.Deliver(laterRequests: null);
        Assert.Same(original, Assert.Single(requests));
        Assert.Equal(20_000, ((Orleans.Serialization.Buffers.PooledBuffer)original.BodyObject!).Length);
    }

    [Fact]
    public async Task RequestWhileTheMigrationOutcomeIsPending_WaitsForTheMigrationAndIsForwarded()
    {
        // The target holds the migration past the source activation's deactivation timeout, and then fails it.
        var acceptArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) =>
        {
            silo.Configure<GrainCollectionOptions>(options => options.DeactivationTimeout = DeactivationTimeout);
            silo.AddIncomingGrainCallFilter(async context =>
            {
                if (context.Request.GetMethodName() == nameof(IActivationMigrationManagerSystemTarget.AcceptMigratingGrains))
                {
                    acceptArrived.TrySetResult();
                    await releaseFailure.Task;
                    throw new InvalidOperationException("The migration fails.");
                }

                await context.Invoke();
            });
        }));
        var source = cluster.Silos[1];
        var target = cluster.Silos[2];
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grain = cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>().GetGrain<IRetirementHandoffGrain>(key, typeof(RetirementHandoffGrain).FullName);
        await PlaceOnAsync(source, () => grain.GetLocation());
        var activation = Assert.IsType<ActivationData>(FindActivation(source, grain.GetGrainId()));

        var running = grain.Block().AsTask();
        await state.BlockEntered.Task.WaitAsync(Timeout, Cancellation);
        var queued = grain.HoldThenGetLocation().AsTask();
        await WaitUntilAsync(() => GetWaitingCount(source, grain.GetGrainId()) == 1);

        await grain.MigrateOnIdleTo(target.SiloAddress);
        state.ReleaseBlock.TrySetResult();
        await running.WaitAsync(Timeout, Cancellation);

        Task<SiloAddress> later;
        try
        {
            // After the source activation's deactivation timeout, a call from the source's silo reaches the source activation, which is still in the catalog,
            // rather than a new activation, and waits there while the migration awaits the target.
            await acceptArrived.Task.WaitAsync(Timeout, Cancellation);
            await Task.Delay(DeactivationTimeout, Cancellation);
            var fromSource = source.ServiceProvider.GetRequiredService<IGrainFactory>().GetGrain<IRetirementHandoffGrain>(key, typeof(RetirementHandoffGrain).FullName);
            later = fromSource.GetLocation().AsTask();
            await WaitUntilAsync(() => activation.WaitingCount == 1);
            Assert.Same(activation, FindActivation(source, grain.GetGrainId()));
            Assert.Null(FindActivation(target, grain.GetGrainId()));
        }
        finally
        {
            releaseFailure.TrySetResult();
        }

        // Once the migration fails, the source rejects the request which the migration carried, and forwards the later call, which runs on a new activation.
        await Assert.ThrowsAsync<OrleansMessageRejectionException>(() => queued.WaitAsync(Timeout, Cancellation));
        await later.WaitAsync(Timeout, Cancellation);
        Assert.Equal(0, state.HoldCount);
    }

    [Fact]
    public async Task MigrationQueuedBehindABlockedBatch_IsCanceledWithoutBeingSent()
    {
        // The target holds the first migration's batch, so the migration manager's pump to the target waits for it.
        var acceptArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAccept = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new ConcurrentQueue<GrainId>();
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) => silo.AddIncomingGrainCallFilter(async context =>
        {
            if (context.Request.GetMethodName() == nameof(IActivationMigrationManagerSystemTarget.AcceptMigratingGrains))
            {
                foreach (var package in (List<GrainMigrationPackage>)context.Request.GetArgument(0)!)
                {
                    received.Enqueue(package.GrainId);
                }

                acceptArrived.TrySetResult();
                await releaseAccept.Task;
            }

            await context.Invoke();
        })));
        var source = cluster.Silos[1];
        var target = cluster.Silos[2];
        var migrationManager = source.ServiceProvider.GetRequiredService<ActivationMigrationManager>();
        var pool = source.ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var grainFactory = cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>();
        var blockedGrain = grainFactory.GetGrain<IRetirementHandoffGrain>(Guid.NewGuid(), typeof(RetirementHandoffGrain).FullName).GetGrainId();
        var queuedGrain = grainFactory.GetGrain<IRetirementHandoffGrain>(Guid.NewGuid(), typeof(RetirementHandoffGrain).FullName).GetGrainId();

        var blocked = migrationManager.StartMigration(target.SiloAddress, CreateSourceAddress(source, blockedGrain), new MigrationContext(pool), [CreateOneWayRequest(blockedGrain, 1)], CancellationToken.None);
        await acceptArrived.Task.WaitAsync(Timeout, Cancellation);

        // The second migration waits behind the first. Its activation's deadline passes before the pump reaches it, so it completes without being sent.
        using var deadline = new CancellationTokenSource();
        var queued = migrationManager.StartMigration(target.SiloAddress, CreateSourceAddress(source, queuedGrain), new MigrationContext(pool), [CreateOneWayRequest(queuedGrain, 2)], deadline.Token);
        try
        {
            deadline.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.Completion.WaitAsync(Timeout, Cancellation));
            Assert.False(blocked.Completion.IsCompleted);
        }
        finally
        {
            releaseAccept.TrySetResult();
        }

        await blocked.Completion.WaitAsync(Timeout, Cancellation);
        Assert.Equal([blockedGrain], received);
    }

    [Fact]
    public async Task AcceptedMigrationWhoseRetargetIsHeld_CompletesAtItsDeadline()
    {
        // The target takes the migration's requests, and the caller's silo holds the retarget of their calls.
        var retargetArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRetarget = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) => silo.AddIncomingGrainCallFilter(async context =>
        {
            if (context.Request.GetMethodName() == nameof(IActivationMigrationManagerSystemTarget.RetargetMigratedRequests))
            {
                retargetArrived.TrySetResult();
                await releaseRetarget.Task;
            }

            await context.Invoke();
        })));
        var source = cluster.Silos[1];
        var target = cluster.Silos[2];
        var migrationManager = source.ServiceProvider.GetRequiredService<ActivationMigrationManager>();
        var grainId = cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>().GetGrain<IRetirementHandoffGrain>(Guid.NewGuid(), typeof(RetirementHandoffGrain).FullName).GetGrainId();

        using var deadline = new CancellationTokenSource();
        var migration = migrationManager.StartMigration(
            target.SiloAddress,
            CreateSourceAddress(source, grainId),
            new MigrationContext(source.ServiceProvider.GetRequiredService<SerializerSessionPool>()),
            [CreateRequest(cluster, grainId, 1, new List<string> { "value" })],
            deadline.Token);
        try
        {
            // The activation's deadline passes while the retarget is held. The target owns the requests, so the migration completes, and the requests are not resent.
            await retargetArrived.Task.WaitAsync(Timeout, Cancellation);
            deadline.Cancel();
            await migration.Completion.WaitAsync(Timeout, Cancellation);
            Assert.True(migration.Completion.IsCompletedSuccessfully);
        }
        finally
        {
            releaseRetarget.TrySetResult();
        }
    }

    [Fact]
    public async Task FailedMigrationOfRequestFromCallerOnTheSource_IsRejectedToItsCaller()
    {
        // The target fails the migration.
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) => silo.AddIncomingGrainCallFilter(async context =>
        {
            if (context.Request.GetMethodName() == nameof(IActivationMigrationManagerSystemTarget.AcceptMigratingGrains))
            {
                throw new InvalidOperationException("The migration fails.");
            }

            await context.Invoke();
        })));
        var source = cluster.Silos[1];
        var target = cluster.Silos[2];
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grain = source.ServiceProvider.GetRequiredService<IGrainFactory>().GetGrain<IRetirementHandoffGrain>(key, typeof(RetirementHandoffGrain).FullName);
        await PlaceOnAsync(source, () => grain.GetLocation());

        // A caller on the source passes a pooled buffer, which its request holds until the migration sends it. Sending it returns the buffer's storage to its pool.
        var running = grain.Block().AsTask();
        await state.BlockEntered.Task.WaitAsync(Timeout, Cancellation);
        var payload = Enumerable.Repeat((byte)0x5A, 20_000).ToArray();
        var echo = grain.EchoPooledPayload(CreatePooledBuffer(payload)).AsTask();
        await WaitUntilAsync(() => GetWaitingCount(source, grain.GetGrainId()) == 1);

        await grain.MigrateOnIdleTo(target.SiloAddress);
        state.ReleaseBlock.TrySetResult();
        await running.WaitAsync(Timeout, Cancellation);

        // The request did not run, so the source rejects it and its caller can retry it.
        await Assert.ThrowsAsync<OrleansMessageRejectionException>(() => echo.WaitAsync(Timeout, Cancellation));
    }

    [Fact]
    public async Task DestinationWithoutMigratedRequestSupport_ReceivesAnOrdinaryMigrationAndRequestsAreForwarded()
    {
        // The source sees the target's manifest without the version of IActivationMigrationManagerSystemTarget which accepts requests, as it would for a silo running an earlier version.
        var earlierVersionSilos = new ConcurrentDictionary<SiloAddress, bool>();
        var calls = new ConcurrentQueue<string>();
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) =>
        {
            silo.Services.AddSingleton<IClusterManifestProvider>(services => new EarlierVersionManifestProvider(
                services.GetRequiredService<Orleans.Runtime.Metadata.ClusterManifestProvider>(),
                services.GetRequiredService<Orleans.Metadata.GrainInterfaceTypeResolver>().GetGrainInterfaceType(typeof(IActivationMigrationManagerSystemTarget)),
                earlierVersionSilos));
            silo.AddIncomingGrainCallFilter(async context =>
            {
                if (context.Request.GetMethodName() is nameof(IActivationMigrationManagerSystemTarget.AcceptMigratingGrains)
                    or nameof(IActivationMigrationManagerSystemTarget.RetargetMigratedRequests))
                {
                    calls.Enqueue(context.Request.GetMethodName());
                }

                if (context.Request.GetMethodName() == nameof(IActivationMigrationManagerSystemTarget.AcceptMigratingGrains))
                {
                    var packages = (List<GrainMigrationPackage>)context.Request.GetArgument(0)!;
                    Assert.All(packages, package => Assert.Null(package.Requests));
                }

                await context.Invoke();
            });
        }));
        var source = cluster.Silos[1];
        var target = cluster.Silos[2];
        earlierVersionSilos[target.SiloAddress] = true;
        var grainFactory = cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>();
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grain = grainFactory.GetGrain<IRetirementHandoffGrain>(key, typeof(RetirementHandoffGrain).FullName);
        await PlaceOnAsync(source, () => grain.GetLocation());

        var running = grain.Block().AsTask();
        await state.BlockEntered.Task.WaitAsync(Timeout, Cancellation);
        var queued = grain.HoldThenGetLocation().AsTask();
        await WaitUntilAsync(() => GetWaitingCount(source, grain.GetGrainId()) == 1);

        // The grain migrates with the ordinary protocol, without its requests.
        await grain.MigrateOnIdleTo(target.SiloAddress);
        state.ReleaseBlock.TrySetResult();
        await running.WaitAsync(Timeout, Cancellation);

        // The source keeps the request unchanged and forwards it to the migrated activation, and its caller is not retargeted.
        Assert.Equal(target.SiloAddress, await state.HoldEntered.Task.WaitAsync(Timeout, Cancellation));
        state.ReleaseHold.TrySetResult();
        Assert.Equal(target.SiloAddress, await queued.WaitAsync(Timeout, Cancellation));
        Assert.Equal(1, state.HoldCount);
        Assert.Equal([nameof(IActivationMigrationManagerSystemTarget.AcceptMigratingGrains)], calls);
    }

    [Fact]
    public async Task MigratedRequestsForAGrainWhichIsAlreadyActive_ReachItsActivation()
    {
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) =>
        {
            silo.Services.AddSingleton<OneWayCacheUpdateObserver>();
            silo.Services.AddSingleton<IMessageStatisticsSink>(services => services.GetRequiredService<OneWayCacheUpdateObserver>());
        }));
        var caller = cluster.Silos[0];
        var previous = cluster.Silos[2];
        var silo = cluster.Silos[1];
        var grainFactory = caller.ServiceProvider.GetRequiredService<IGrainFactory>();
        var locator = caller.ServiceProvider.GetRequiredService<Orleans.Runtime.GrainDirectory.GrainLocator>();
        var cacheUpdates = silo.ServiceProvider.GetRequiredService<OneWayCacheUpdateObserver>();
        var migrationManager = silo.ServiceProvider.GetRequiredService<ActivationMigrationManager>();
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grain = grainFactory.GetGrain<IRetirementHandoffGrain>(key, typeof(RetirementHandoffGrain).FullName);
        await PlaceOnAsync(silo, () => grain.GetLocation());
        var activation = Assert.IsType<ActivationData>(FindActivation(silo, grain.GetGrainId()));
        var running = grain.Block().AsTask();
        await state.BlockEntered.Task.WaitAsync(Timeout, Cancellation);

        // The caller's cache holds the migrating activation's address, which the migration replaces.
        var previousAddress = new GrainAddress { GrainId = grain.GetGrainId(), SiloAddress = previous.SiloAddress, ActivationId = ActivationId.NewId() };
        locator.UpdateCache(grain.GetGrainId(), previous.SiloAddress);
        cacheUpdates.GrainId = grain.GetGrainId();

        // The migration arrives too late to create the activation, so the existing activation receives the request as it receives a forwarded request.
        var request = CreateOneWayRequest(grain.GetGrainId(), 1);
        request.SendingSilo = caller.SiloAddress;
        var context = new MigrationContext(silo.ServiceProvider.GetRequiredService<SerializerSessionPool>());
        context.TryAddValue(ActivationData.GrainAddressMigrationContextKey, previousAddress);
        GrainMigrationPackage package = new()
        {
            GrainId = grain.GetGrainId(),
            MigrationContext = context,
            Requests = [request],
        };
        await migrationManager.AcceptMigratingGrains([package], Cancellation);
        Assert.Same(activation, FindActivation(silo, grain.GetGrainId()));
        Assert.Equal(1, activation.WaitingCount);
        Assert.Equal(1, request.ForwardCount);

        // As for a forwarded one-way request, the silo sends the cache update to the request's caller, which receives no response.
        await cacheUpdates.Received.Task.WaitAsync(Timeout, Cancellation);
        await WaitUntilAsync(() => locator.TryLookupInCache(grain.GetGrainId(), out var cached) && silo.SiloAddress.Equals(cached.SiloAddress));

        state.ReleaseBlock.TrySetResult();
        await running.WaitAsync(Timeout, Cancellation);
    }

    [Fact]
    public async Task MigratedRequests_UseOrdinaryPerRequestOverloadAdmission()
    {
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) =>
            silo.Configure<SiloMessagingOptions>(options => options.MaxEnqueuedRequestsHardLimit = 1)));
        var silo = cluster.Silos[1];
        var grainFactory = cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>();
        var migrationManager = silo.ServiceProvider.GetRequiredService<ActivationMigrationManager>();
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grainId = grainFactory.GetGrain<IRetirementHandoffGrain>(key, typeof(RetirementHandoffGrain).FullName).GetGrainId();

        // The new activation is held while activating, so the requests which it admits stay queued.
        state.HoldActivationOn = silo.SiloAddress;
        GrainMigrationPackage package = new()
        {
            GrainId = grainId,
            MigrationContext = new MigrationContext(silo.ServiceProvider.GetRequiredService<SerializerSessionPool>()),
            Requests = [CreateOneWayRequest(grainId, 1), CreateOneWayRequest(grainId, 2), CreateOneWayRequest(grainId, 3)],
        };
        var accepting = migrationManager.AcceptMigratingGrains([package], Cancellation).AsTask();
        try
        {
            // Ordinary admission admits two requests with a hard limit of one and rejects the third.
            await state.ActivationHeld.Task.WaitAsync(Timeout, Cancellation);
            var activation = Assert.IsType<ActivationData>(FindActivation(silo, grainId));
            // Admission finishes before activation is scheduled, so the held activation provides the observation barrier.
            Assert.Equal(2, activation.WaitingCount);
        }
        finally
        {
            state.ReleaseActivation.TrySetResult();
        }

        await accepting.WaitAsync(Timeout, Cancellation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MigratedRequests_UseOrdinaryAdmissionForAnExistingActivation(bool deactivating)
    {
        var arrived = new TaskCompletionSource<GrainMigrationPackage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAcceptance = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) => silo.AddIncomingGrainCallFilter(async context =>
        {
            if (context.Request.GetMethodName() == nameof(IActivationMigrationManagerSystemTarget.AcceptMigratingGrains))
            {
                arrived.TrySetResult(Assert.Single((List<GrainMigrationPackage>)context.Request.GetArgument(0)!));
                await releaseAcceptance.Task;
            }

            await context.Invoke();
        })));
        var caller = cluster.Silos[0];
        var source = cluster.Silos[1];
        var target = cluster.Silos[2];
        var grainFactory = caller.ServiceProvider.GetRequiredService<IGrainFactory>();
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grain = grainFactory.GetGrain<IRetirementHandoffGrain>(key, typeof(RetirementHandoffGrain).FullName);
        await PlaceOnAsync(source, () => grain.GetLocation());
        var running = grain.Block().AsTask();
        await state.BlockEntered.Task.WaitAsync(Timeout, Cancellation);
        Task migrated;
        RequestContext.Set(IPlacementDirector.PlacementHintKey, caller.SiloAddress);
        try
        {
            // If the destination deactivates, ordinary forwarding places this request on the third silo.
            migrated = grain.Record("migrated").AsTask();
        }
        finally
        {
            RequestContext.Remove(IPlacementDirector.PlacementHintKey);
        }

        await WaitUntilAsync(() => GetWaitingCount(source, grain.GetGrainId()) == 1);
        state.HoldActivationOn = target.SiloAddress;
        try
        {
            await grain.MigrateOnIdleTo(target.SiloAddress);
            state.ReleaseBlock.TrySetResult();
            await running.WaitAsync(Timeout, Cancellation);
            var package = await arrived.Task.WaitAsync(Timeout, Cancellation);

            // Create the destination independently before the package is delivered, retaining the original package's context.
            Assert.True(package.MigrationContext.TryGetValue(ActivationData.GrainAddressMigrationContextKey, out GrainAddress? previous));
            var context = new MigrationContext(target.ServiceProvider.GetRequiredService<SerializerSessionPool>());
            Assert.True(context.TryAddValue(ActivationData.GrainAddressMigrationContextKey, previous));
            var activation = Assert.IsType<ActivationData>(target.ServiceProvider.GetRequiredService<Catalog>()
                .GetOrCreateActivation(package.GrainId, null, new GrainMigrationPackage { GrainId = package.GrainId, MigrationContext = context }));
            await state.ActivationHeld.Task.WaitAsync(Timeout, Cancellation);

            var local = target.ServiceProvider.GetRequiredService<IGrainFactory>()
                .GetGrain<IRetirementHandoffGrain>(key, typeof(RetirementHandoffGrain).FullName);
            var direct = local.Record("direct").AsTask();
            await WaitUntilAsync(() => activation.WaitingCount == 1);

            if (deactivating)
            {
                state.ReleaseActivation.TrySetResult();
                await direct.WaitAsync(Timeout, Cancellation);
                var holding = local.HoldThenGetLocation().AsTask();
                await state.HoldEntered.Task.WaitAsync(Timeout, Cancellation);
                activation.Deactivate(new DeactivationReason(DeactivationReasonCode.ActivationIdle, "Test ordinary admission during deactivation"), Cancellation);
                Assert.Equal(ActivationState.Deactivating, activation.State);
                releaseAcceptance.TrySetResult();
                await WaitUntilAsync(() => activation.WaitingCount == 1);
                state.ReleaseHold.TrySetResult();
                await holding.WaitAsync(Timeout, Cancellation);
                await activation.Deactivated.WaitAsync(Timeout, Cancellation);
            }
            else
            {
                releaseAcceptance.TrySetResult();
                await WaitUntilAsync(() => activation.WaitingCount == 2);
                state.ReleaseActivation.TrySetResult();
                await direct.WaitAsync(Timeout, Cancellation);
            }

            await migrated.WaitAsync(Timeout, Cancellation);
            Assert.Equal(["direct", "migrated"], state.Calls);
            Assert.Equal(deactivating ? caller.SiloAddress : target.SiloAddress, await grain.GetLocation().AsTask().WaitAsync(Timeout, Cancellation));
        }
        finally
        {
            releaseAcceptance.TrySetResult();
            state.ReleaseBlock.TrySetResult();
            state.ReleaseActivation.TrySetResult();
            state.ReleaseHold.TrySetResult();
        }
    }

    [Fact]
    public async Task MigratedRequests_AreRejectedWhenDestinationActivationFails()
    {
        await using var cluster = await DeployAsync();
        var source = cluster.Silos[1];
        var target = cluster.Silos[2];
        var grainFactory = cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>();
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grain = grainFactory.GetGrain<IRetirementHandoffGrain>(key, typeof(RetirementHandoffGrain).FullName);
        await PlaceOnAsync(source, () => grain.GetLocation());
        state.FailActivationOn = target.SiloAddress;
        var running = grain.Block().AsTask();
        await state.BlockEntered.Task.WaitAsync(Timeout, Cancellation);
        var queued = grain.Record("queued").AsTask();
        await WaitUntilAsync(() => GetWaitingCount(source, grain.GetGrainId()) == 1);
        try
        {
            await grain.MigrateOnIdleTo(target.SiloAddress);
            state.ReleaseBlock.TrySetResult();
            await running.WaitAsync(Timeout, Cancellation);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => queued.WaitAsync(Timeout, Cancellation));
            Assert.Equal("Destination activation failed.", error.Message);
            Assert.Empty(state.Calls);
        }
        finally
        {
            state.ReleaseBlock.TrySetResult();
        }
    }

    [Fact]
    public async Task MigratedRequestWhichExpiredInTransit_IsDroppedAndTheOthersAreAccepted()
    {
        var received = new TaskCompletionSource<(Message Expired, Message Live)>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) => silo.AddIncomingGrainCallFilter(async context =>
        {
            if (context.Request.GetMethodName() == nameof(IActivationMigrationManagerSystemTarget.AcceptMigratingGrains))
            {
                var package = Assert.Single((List<GrainMigrationPackage>)context.Request.GetArgument(0)!);
                var requests = Assert.IsType<List<Message>>(package.Requests);
                Assert.Equal(2, requests.Count);
                requests[0].TimeToLive = TimeSpan.FromSeconds(-1);
                received.TrySetResult((requests[0], requests[1]));
            }

            await context.Invoke();
        })));
        var caller = cluster.Silos[0];
        var source = cluster.Silos[1];
        var target = cluster.Silos[2];
        var grainFactory = caller.ServiceProvider.GetRequiredService<IGrainFactory>();
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grain = grainFactory.GetGrain<IRetirementHandoffGrain>(key, typeof(RetirementHandoffGrain).FullName);
        await PlaceOnAsync(source, () => grain.GetLocation());
        var running = grain.Block().AsTask();
        await state.BlockEntered.Task.WaitAsync(Timeout, Cancellation);
        var expiredCall = grain.Record("expired").AsTask();
        await WaitUntilAsync(() => GetWaitingCount(source, grain.GetGrainId()) == 1);
        var expiredCallback = FindPendingCallback(caller, grain.GetGrainId(), nameof(IRetirementHandoffGrain.Record));
        var liveCall = grain.Record("live").AsTask();
        await WaitUntilAsync(() => GetWaitingCount(source, grain.GetGrainId()) == 2);

        try
        {
            await grain.MigrateOnIdleTo(target.SiloAddress);
            state.ReleaseBlock.TrySetResult();
            await running.WaitAsync(Timeout, Cancellation);
            var requests = await received.Task.WaitAsync(Timeout, Cancellation);
            expiredCallback.OnTimeout();
            await Assert.ThrowsAsync<TimeoutException>(() => expiredCall.WaitAsync(Timeout, Cancellation));
            await liveCall.WaitAsync(Timeout, Cancellation);
            Assert.Null(requests.Expired._bodyObject);
            Assert.Equal(0, requests.Expired.ForwardCount);
            Assert.Equal(1, requests.Live.ForwardCount);
            Assert.Equal(["live"], state.Calls);
            Assert.Equal(target.SiloAddress, await grain.GetLocation().AsTask().WaitAsync(Timeout, Cancellation));
        }
        finally
        {
            state.ReleaseBlock.TrySetResult();
        }
    }

    [Fact]
    public async Task MigrationBatch_OutlivesItsTimeoutWithoutBeingReusedByTheNextInvocation()
    {
        var firstSubmitted = new TaskCompletionSource<List<GrainMigrationPackage>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondSubmitted = new TaskCompletionSource<List<GrainMigrationPackage>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serializing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseSerialization = new ManualResetEventSlim();
        var invocationCount = 0;
        var gate = new MigrationSerializationGate
        {
            BeforeSerialization = () =>
            {
                serializing.TrySetResult();
                releaseSerialization.Wait(Cancellation);
            }
        };
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) =>
        {
            silo.Services.AddSingleton<MigrationSerializationCallbacks>();
            silo.AddOutgoingGrainCallFilter(async context =>
            {
                if (context.Request.GetMethodName() == nameof(IActivationMigrationManagerSystemTarget.AcceptMigratingGrains))
                {
                    var batch = (List<GrainMigrationPackage>)context.Request.GetArgument(0)!;
                    switch (Interlocked.Increment(ref invocationCount))
                    {
                        case 1:
                            firstSubmitted.TrySetResult(batch);
                            // Block the headers before the RPC's batch argument can be serialized.
                            RequestContext.Set("migration-serialization-gate", gate);
                            try
                            {
                                await context.Invoke();
                            }
                            finally
                            {
                                RequestContext.Remove("migration-serialization-gate");
                            }

                            return;
                        case 2:
                            secondSubmitted.TrySetResult(batch);
                            break;
                    }
                }

                await context.Invoke();
            });
        }));
        var caller = cluster.Silos[0];
        var source = cluster.Silos[1];
        var target = cluster.Silos[2];
        var grainFactory = caller.ServiceProvider.GetRequiredService<IGrainFactory>();
        var firstKey = Guid.NewGuid();
        var secondKey = Guid.NewGuid();
        var firstState = RetirementHandoffState.Create(firstKey);
        var secondState = RetirementHandoffState.Create(secondKey);
        var first = grainFactory.GetGrain<IRetirementHandoffGrain>(firstKey, typeof(RetirementHandoffGrain).FullName);
        var second = grainFactory.GetGrain<IRetirementHandoffGrain>(secondKey, typeof(RetirementHandoffGrain).FullName);
        await PlaceOnAsync(source, () => first.GetLocation());
        await PlaceOnAsync(source, () => second.GetLocation());
        var firstRunning = first.Block().AsTask();
        var secondRunning = second.Block().AsTask();
        await Task.WhenAll(firstState.BlockEntered.Task, secondState.BlockEntered.Task).WaitAsync(Timeout, Cancellation);
        var firstCall = first.Record("first").AsTask();
        var secondCall = second.Record("second").AsTask();
        await WaitUntilAsync(() => GetWaitingCount(source, first.GetGrainId()) == 1 && GetWaitingCount(source, second.GetGrainId()) == 1);

        try
        {
            await first.MigrateOnIdleTo(target.SiloAddress);
            firstState.ReleaseBlock.TrySetResult();
            await firstRunning.WaitAsync(Timeout, Cancellation);
            var firstBatch = await firstSubmitted.Task.WaitAsync(Timeout, Cancellation);
            await serializing.Task.WaitAsync(Timeout, Cancellation);

            // Trigger the caller's timeout while its outbound writer still owns the unsent invocation.
            var remote = source.ServiceProvider.GetRequiredService<IInternalGrainFactory>()
                .GetSystemTarget<IActivationMigrationManagerSystemTarget>(Constants.ActivationMigratorType, target.SiloAddress);
            FindPendingCallback(source, remote.GetGrainId()).OnTimeout();
            await Assert.ThrowsAsync<OrleansMessageRejectionException>(() => firstCall.WaitAsync(Timeout, Cancellation));

            await second.MigrateOnIdleTo(target.SiloAddress);
            secondState.ReleaseBlock.TrySetResult();
            await secondRunning.WaitAsync(Timeout, Cancellation);
            var secondBatch = await secondSubmitted.Task.WaitAsync(Timeout, Cancellation);
            Assert.NotSame(firstBatch, secondBatch);
            Assert.Equal(first.GetGrainId(), Assert.Single(firstBatch).GrainId);
            Assert.Equal(second.GetGrainId(), Assert.Single(secondBatch).GrainId);
        }
        finally
        {
            releaseSerialization.Set();
            firstState.ReleaseBlock.TrySetResult();
            secondState.ReleaseBlock.TrySetResult();
        }

        await secondCall.WaitAsync(Timeout, Cancellation);
        Assert.Equal(target.SiloAddress, await first.GetLocation().AsTask().WaitAsync(Timeout, Cancellation));
        Assert.Equal(["first"], firstState.Calls);
        Assert.Equal(["second"], secondState.Calls);
        Assert.Equal(2, Volatile.Read(ref invocationCount));
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

        // The carried request was admitted before directory registration, so this later direct request follows it.
        await Task.WhenAll(queued, direct).WaitAsync(Timeout, Cancellation);
        Assert.Equal(["queued", "direct"], state.Calls);
    }

    [Fact]
    public async Task MigrationRoutes_DoNotOverwriteALaterRetarget()
    {
        // Hold the destination's acknowledgement, so that a later route for the same call arrives while the migration awaits acceptance.
        var accepted = new TaskCompletionSource<(GrainId SendingGrain, CorrelationId Id)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAcceptance = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) => silo.AddIncomingGrainCallFilter(async context =>
        {
            await context.Invoke();
            if (context.Request.GetMethodName() == nameof(IActivationMigrationManagerSystemTarget.AcceptMigratingGrains))
            {
                var package = Assert.Single((List<GrainMigrationPackage>)context.Request.GetArgument(0)!);
                var request = Assert.Single(package.Requests!);
                accepted.TrySetResult((request.SendingGrain, request.Id));
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
    public async Task MigrationUnderwayWhenRetiring_CarriesQueuedRequests()
    {
        // Hold the destination's acceptance of the migrating activation, so that retirement begins while the migration is underway.
        var acceptArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAccept = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) => silo.AddIncomingGrainCallFilter(async context =>
        {
            if (context.Request.GetMethodName() == nameof(IActivationMigrationManagerSystemTarget.AcceptMigratingGrains))
            {
                acceptArrived.TrySetResult();
                await releaseAccept.Task;
            }

            await context.Invoke();
        })));
        var retiring = cluster.Silos[1];
        var target = cluster.Silos[2];
        var grainFactory = cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>();
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
            using var stages = new SiloStageObserver(retiring.SiloAddress, ServiceLifecycleStage.GrainDeactivation, releaseAccept.Task);
            stopping = cluster.StopSiloAsync(retiring, Cancellation);
            await stages.Stopping.WaitAsync(Timeout, Cancellation);
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
    public async Task CancellationReachingTheActivatingNewActivation_CancelsTheMigratedRequestBeforeItRuns()
    {
        await using var cluster = await DeployAsync(ExtendShutdownTimeouts);
        var caller = cluster.Silos[0];
        var source = cluster.Silos[1];
        var target = cluster.Silos[2];
        var grainFactory = caller.ServiceProvider.GetRequiredService<IGrainFactory>();
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grain = grainFactory.GetGrain<IRetirementHandoffGrain>(key, typeof(RetirementHandoffGrain).FullName);
        await PlaceOnAsync(source, () => grain.GetLocation());

        var running = grain.Block().AsTask();
        await state.BlockEntered.Task.WaitAsync(Timeout, Cancellation);
        var call = grain.HoldUntilCanceled(CancellationToken.None).AsTask();
        await WaitUntilAsync(() => GetWaitingCount(source, grain.GetGrainId()) == 1);

        // The grain migrates with its queued request, and its new activation is held while activating, with the request queued.
        state.HoldActivationOn = target.SiloAddress;
        await grain.MigrateOnIdleTo(target.SiloAddress);
        state.ReleaseBlock.TrySetResult();
        await running.WaitAsync(Timeout, Cancellation);
        var (sender, messageId) = FindPendingCall(caller, grain.GetGrainId());
        try
        {
            await state.ActivationHeld.Task.WaitAsync(Timeout, Cancellation);
            var activation = Assert.IsType<ActivationData>(FindActivation(target, grain.GetGrainId()));
            Assert.Equal(1, activation.WaitingCount);

            // The new activation queues migrated requests before it activates, so a cancellation which reaches it now cancels the request before it runs.
            await ((IGrainCallCancellationExtension)activation).CancelRequestAsync(sender, messageId, CancellationToken.None).AsTask().WaitAsync(Timeout, Cancellation);
            Assert.Equal(0, activation.WaitingCount);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(Timeout, Cancellation));

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedOneWayRequest_MigratesWithoutRetargetingItsCaller(bool retireSource)
    {
        var retargets = 0;
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) =>
        {
            silo.Services.AddSingleton<OneWayCacheUpdateObserver>();
            silo.Services.AddSingleton<IMessageStatisticsSink>(services => services.GetRequiredService<OneWayCacheUpdateObserver>());
            silo.AddIncomingGrainCallFilter(async context =>
            {
                if (context.Request.GetMethodName() == nameof(IActivationMigrationManagerSystemTarget.RetargetMigratedRequests))
                {
                    Interlocked.Increment(ref retargets);
                }

                await context.Invoke();
            });
        }));
        var caller = cluster.Silos[0];
        var source = cluster.Silos[1];
        var target = cluster.Silos[2];
        var grainFactory = caller.ServiceProvider.GetRequiredService<IGrainFactory>();
        var locator = caller.ServiceProvider.GetRequiredService<Orleans.Runtime.GrainDirectory.GrainLocator>();
        var cacheUpdates = caller.ServiceProvider.GetRequiredService<OneWayCacheUpdateObserver>();
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grain = grainFactory.GetGrain<IRetirementHandoffGrain>(key, typeof(RetirementHandoffGrain).FullName);
        await PlaceOnAsync(source, () => grain.GetLocation());
        Assert.True(locator.TryLookupInCache(grain.GetGrainId(), out var cached));
        Assert.Equal(source.SiloAddress, cached.SiloAddress);
        cacheUpdates.GrainId = grain.GetGrainId();

        var running = grain.Block().AsTask();
        await state.BlockEntered.Task.WaitAsync(Timeout, Cancellation);
        await grain.RecordOneWay();
        await WaitUntilAsync(() => GetWaitingCount(source, grain.GetGrainId()) == 1);

        var stopping = Task.CompletedTask;
        if (retireSource)
        {
            stopping = cluster.StopSiloAsync(source, Cancellation);
            await WaitUntilAsync(() => FindActivation(source, grain.GetGrainId()) is ActivationData { State: ActivationState.Deactivating });
        }
        else
        {
            await grain.MigrateOnIdleTo(target.SiloAddress);
        }

        state.ReleaseBlock.TrySetResult();
        await running.WaitAsync(Timeout, Cancellation);

        // The one-way request runs on the new silo, and its caller has no pending call to retarget.
        var destination = await state.OneWayReceived.Task.WaitAsync(Timeout, Cancellation);
        Assert.NotEqual(source.SiloAddress, destination);
        if (!retireSource)
        {
            // Keep the source alive so that only the migrated request's cache update corrects the caller's route.
            await cacheUpdates.Received.Task.WaitAsync(Timeout, Cancellation);
            Assert.Equal(target.SiloAddress, destination);
            Assert.True(locator.TryLookupInCache(grain.GetGrainId(), out cached));
            Assert.Equal(destination, cached.SiloAddress);
        }

        await stopping.WaitAsync(Timeout, Cancellation);
        Assert.Equal(0, Volatile.Read(ref retargets));
    }

    private sealed class OneWayCacheUpdateObserver : IMessageStatisticsSink
    {
        public GrainId GrainId { get; set; }
        public TaskCompletionSource Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Action<Message> GetMessageObserver() => message =>
        {
            if (message.Direction is Message.Directions.Response
                && message.Result is Message.ResponseTypes.Rejection
                && message.SendingGrain == GrainId
                && message.HasCacheInvalidationHeader)
            {
                Received.TrySetResult();
            }
        };
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
        ActivationMigration? migration = null;
        await migrationManager.RunOrQueueTask(() =>
        {
            migration = migrationManager.StartMigration(target.SiloAddress, CreateSourceAddress(source, grain.GetGrainId()), context, [request], Cancellation);
            Assert.NotNull(migration.Package.Requests);
            Assert.True(context.TryGetValue("marker", out string? value));
            Assert.Equal(marker, value);
            return Task.CompletedTask;
        });

        await migration!.Completion.WaitAsync(Timeout, Cancellation);
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

        // The request, which this silo received, fits in a message by itself, and so does the migration context, whose size is mostly in its key, but not together.
        var context = new MigrationContext(source.ServiceProvider.GetRequiredService<SerializerSessionPool>());
        context.TryAddValue(new string('k', MaxMessageBodySize / 2), 0);
        using var request = CreateReceivedRequest(cluster, source, grainId, 1, MaxMessageBodySize * 3 / 5);

        // The migration carries the context without the request, which it forwards instead.
        var migration = migrationManager.StartMigration(target.SiloAddress, CreateSourceAddress(source, grainId), context, [request], Cancellation);
        Assert.Null(migration.Package.Requests);
        await migration.Completion.WaitAsync(Timeout, Cancellation);
    }

    [Fact]
    public async Task RequestWhichCannotBeSerialized_FailsItsMigrationAndIsRejected()
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
        var request = CreateReceivedRequest(cluster, source, grainId, 1, 1);
        request.RequestContextData = new() { ["unserializable"] = new UnserializableValue() };

        // The migration's message cannot be serialized, so the migration fails.
        var migration = migrationManager.StartMigration(
            target.SiloAddress,
            CreateSourceAddress(source, grainId),
            new MigrationContext(source.ServiceProvider.GetRequiredService<SerializerSessionPool>()),
            [request],
            Cancellation);
        Assert.NotNull(migration.Package.Requests);
        await Assert.ThrowsAnyAsync<Exception>(() => migration.Completion.WaitAsync(Timeout, Cancellation));
        Assert.Null(FindActivation(target, grainId));

        // The request is rejected and left intact, since the migration's message may still be awaiting serialization.
        migration.Deliver(laterRequests: null);
        Assert.IsType<Orleans.Runtime.Messaging.MessageReadRequest>(request._bodyObject);
    }

    [Fact]
    public async Task MigrationWithoutRequests_CompletesWithoutWaitingForAnotherGrainsRetargets()
    {
        // Hold callers' retargets, so that the accepted grain's migration cannot complete.
        var retargetArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRetarget = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) => silo.AddIncomingGrainCallFilter(async context =>
        {
            if (context.Request.GetMethodName() == nameof(IActivationMigrationManagerSystemTarget.RetargetMigratedRequests))
            {
                retargetArrived.TrySetResult();
                await releaseRetarget.Task;
            }

            await context.Invoke();
        })));
        var caller = cluster.Silos[0];
        var source = cluster.Silos[1];
        var target = cluster.Silos[2];
        var migrationManager = source.ServiceProvider.GetRequiredService<ActivationMigrationManager>();
        var pool = source.ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var grainFactory = caller.ServiceProvider.GetRequiredService<IGrainFactory>();
        var acceptedKey = Guid.NewGuid();
        var otherKey = Guid.NewGuid();
        RetirementHandoffState.Create(acceptedKey);
        RetirementHandoffState.Create(otherKey);
        var acceptedGrain = grainFactory.GetGrain<IRetirementHandoffGrain>(acceptedKey, typeof(RetirementHandoffGrain).FullName).GetGrainId();
        var otherGrain = grainFactory.GetGrain<IRetirementHandoffGrain>(otherKey, typeof(RetirementHandoffGrain).FullName).GetGrainId();

        // Both migrations are queued on the migration manager's scheduler before its pump runs, so they travel in one batch. Only one carries requests.
        ActivationMigration? acceptedMigration = null;
        ActivationMigration? otherMigration = null;
        await migrationManager.RunOrQueueTask(() =>
        {
            acceptedMigration = migrationManager.StartMigration(target.SiloAddress, CreateSourceAddress(source, acceptedGrain), new MigrationContext(pool), [CreateRequest(acceptedGrain, 1)], Cancellation);
            otherMigration = migrationManager.StartMigration(target.SiloAddress, CreateSourceAddress(source, otherGrain), new MigrationContext(pool), requests: null, Cancellation);
            Assert.NotNull(acceptedMigration.Package.Requests);
            Assert.Null(otherMigration.Package.Requests);
            return Task.CompletedTask;
        });

        try
        {
            // While the accepted grain's caller has not acknowledged its retarget, the other grain's migration has already completed, so its source can finish deactivating.
            await retargetArrived.Task.WaitAsync(Timeout, Cancellation);
            await otherMigration!.Completion.WaitAsync(Timeout, Cancellation);
            Assert.False(acceptedMigration!.Completion.IsCompleted);
        }
        finally
        {
            releaseRetarget.TrySetResult();
        }

        await acceptedMigration.Completion.WaitAsync(Timeout, Cancellation);

        Message CreateRequest(GrainId grainId, long id) => new()
        {
            Direction = Message.Directions.Request,
            Id = new CorrelationId(id),
            SendingGrain = GrainId.Create("test", "caller"),
            SendingSilo = caller.SiloAddress,
            TargetGrain = grainId,
            BodyObject = new List<string> { "value" },
        };
    }

    [Fact]
    public async Task ActivationInterruptedByRetirement_DoesNotMigrate()
    {
        var migrationsReceived = 0;
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) => silo.AddIncomingGrainCallFilter(async context =>
        {
            if (context.Request.GetMethodName() == nameof(IActivationMigrationManagerSystemTarget.AcceptMigratingGrains))
            {
                Interlocked.Increment(ref migrationsReceived);
            }

            await context.Invoke();
        })));
        var retiring = cluster.Silos[1];
        var grainFactory = cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>();
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grain = grainFactory.GetGrain<IRetirementHandoffGrain>(key, typeof(RetirementHandoffGrain).FullName);

        // The activation is held in OnActivateAsync, with a request queued behind it.
        state.HoldActivationOn = retiring.SiloAddress;
        RequestContext.Set(IPlacementDirector.PlacementHintKey, retiring.SiloAddress);
        Task call;
        try
        {
            call = grain.Record("queued").AsTask();
        }
        finally
        {
            RequestContext.Remove(IPlacementDirector.PlacementHintKey);
        }

        await state.ActivationHeld.Task.WaitAsync(Timeout, Cancellation);
        var activation = Assert.IsType<ActivationData>(FindActivation(retiring, grain.GetGrainId()));
        await WaitUntilAsync(() => activation.WaitingCount == 1);

        // Retirement interrupts the activation. Its state may be partially initialized, so it does not migrate, and its queued request is forwarded or rejected as before.
        var stopping = cluster.StopSiloAsync(retiring, Cancellation);
        await WaitUntilAsync(() => activation.State is ActivationState.Deactivating or ActivationState.Invalid);
        state.ReleaseActivation.TrySetResult();
        await stopping.WaitAsync(Timeout, Cancellation);
        await call.WaitAsync(Timeout, Cancellation).ContinueWith(static _ => { }, TaskScheduler.Default);
        Assert.Equal(0, Volatile.Read(ref migrationsReceived));
    }

    private static Message CreateRequest(InProcessTestCluster cluster, GrainId grainId, long id, object body) => new()
    {
        Direction = Message.Directions.Request,
        Id = new CorrelationId(id),
        SendingGrain = GrainId.Create("test", "caller"),
        SendingSilo = cluster.Silos[0].SiloAddress,
        TargetGrain = grainId,
        BodyObject = body,
    };

    private static Orleans.Serialization.Buffers.PooledBuffer CreatePooledBuffer(byte[] contents)
    {
        var buffer = new Orleans.Serialization.Buffers.PooledBuffer();
        buffer.Write(contents);
        return buffer;
    }

    /// <summary>
    /// Creates a request as this silo receives one, which holds its serialized body.
    /// </summary>
    private static Message CreateReceivedRequest(InProcessTestCluster cluster, InProcessSiloHandle silo, GrainId grainId, long id, int bodyLength)
    {
        using var bodyWriter = new Orleans.Serialization.Buffers.ArcBufferWriter();
        bodyWriter.Write(new byte[bodyLength]);
        var readRequest = silo.ServiceProvider.GetRequiredService<Orleans.Runtime.Messaging.MessageHandlerShared>().GetReceiveMessageHandler();
        readRequest.Initialize(default, bodyWriter.ConsumeSlice(bodyLength));
        var request = new Message
        {
            Direction = Message.Directions.Request,
            Id = new CorrelationId(id),
            SendingGrain = GrainId.Create("test", "caller"),
            SendingSilo = cluster.Silos[0].SiloAddress,
            TargetGrain = grainId,
        };
        request.SetMessageReadRequest(readRequest);
        return request;
    }

    private static GrainAddress CreateSourceAddress(InProcessSiloHandle silo, GrainId grainId)
        => new() { GrainId = grainId, SiloAddress = silo.SiloAddress, ActivationId = ActivationId.NewId() };

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

        // Each request, which this silo received, fits in its own message, but not together with the other, so the migration carries neither.
        using var first = CreateReceivedRequest(cluster, source, grainId, 1, MaxMessageBodySize * 3 / 5);
        using var second = CreateReceivedRequest(cluster, source, grainId, 2, MaxMessageBodySize * 3 / 5);
        var context = new MigrationContext(source.ServiceProvider.GetRequiredService<SerializerSessionPool>());
        var migration = migrationManager.StartMigration(target.SiloAddress, CreateSourceAddress(source, grainId), context, [first, second], Cancellation);
        Assert.Null(migration.Package.Requests);

        // The grain migrates without them.
        await migration.Completion.WaitAsync(Timeout, Cancellation);
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

    private static readonly TimeSpan DeactivationTimeout = TimeSpan.FromSeconds(3);

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

    private static (GrainId Sender, CorrelationId Id) FindPendingCall(InProcessSiloHandle silo, GrainId target)
    {
        var message = FindPendingCallback(silo, target).Message;
        return (message.SendingGrain, message.Id);
    }

    private static CallbackData FindPendingCallback(InProcessSiloHandle silo, GrainId target, string? methodName = null)
    {
        var callbacks = (ConcurrentDictionary<(GrainId, CorrelationId), CallbackData>)typeof(InsideRuntimeClient)
            .GetField("callbacks", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(silo.ServiceProvider.GetRequiredService<InsideRuntimeClient>())!;
        return Assert.Single(callbacks.Values, callback => callback.Message.TargetGrain == target
            && (methodName is null || callback.Message.BodyObject is Orleans.Serialization.Invocation.IInvokable request && request.GetMethodName() == methodName));
    }

    private static Message CreateOneWayRequest(GrainId grainId, long id) => new()
    {
        Direction = Message.Directions.OneWay,
        Id = new CorrelationId(id),
        SendingGrain = GrainId.Create("test", "caller"),
        TargetGrain = grainId,
        TimeToLive = TimeSpan.FromMinutes(1),
    };

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

[GenerateSerializer, Immutable, SerializationCallbacks(typeof(MigrationSerializationCallbacks))]
public sealed class MigrationSerializationGate
{
    [NonSerialized]
    public Action? BeforeSerialization;
}

public sealed class MigrationSerializationCallbacks
{
    public void OnSerializing(MigrationSerializationGate value) => value.BeforeSerialization?.Invoke();
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

    ValueTask<byte[]> EchoPooledPayload(Orleans.Serialization.Buffers.PooledBuffer payload);
}

public interface IGenericRetirementHandoffGrain<T> : IGrainWithGuidKey
{
    ValueTask<SiloAddress> GetLocation();

    ValueTask Block();

    ValueTask<SiloAddress> HoldThenGetLocation();
}

[PreferLocalPlacement]
public sealed class GenericRetirementHandoffGrain<T> : Grain, IGenericRetirementHandoffGrain<T>
{
    private RetirementHandoffState State => RetirementHandoffState.Get(this.GetPrimaryKey());

    public ValueTask<SiloAddress> GetLocation() => new(GrainContext.Address.SiloAddress!);

    public async ValueTask Block()
    {
        State.BlockEntered.TrySetResult();
        await State.ReleaseBlock.Task;
    }

    public async ValueTask<SiloAddress> HoldThenGetLocation()
    {
        var location = GrainContext.Address.SiloAddress!;
        State.HoldEntered.TrySetResult(location);
        await State.ReleaseHold.Task;
        return location;
    }
}

public interface IStatelessRetirementHandoffGrain : IGrainWithGuidKey
{
    ValueTask Ping();
}

[StatelessWorker]
public sealed class StatelessRetirementHandoffGrain : Grain, IStatelessRetirementHandoffGrain
{
    public ValueTask Ping() => default;
}

/// <summary>
/// Presents the cluster manifest with the version of <see cref="IActivationMigrationManagerSystemTarget"/> removed from the manifests of selected silos,
/// as a silo sees silos which run an earlier version, which did not version the interface.
/// </summary>
internal sealed class EarlierVersionManifestProvider(
    IClusterManifestProvider inner,
    GrainInterfaceType migrationInterface,
    ConcurrentDictionary<SiloAddress, bool> earlierVersionSilos) : IClusterManifestProvider
{
    public ClusterManifest Current => Transform(inner.Current);

    public IAsyncEnumerable<ClusterManifest> Updates => inner.Updates;

    public GrainManifest LocalGrainManifest => inner.LocalGrainManifest;

    private ClusterManifest Transform(ClusterManifest manifest)
    {
        if (earlierVersionSilos.IsEmpty)
        {
            return manifest;
        }

        var silos = manifest.Silos;
        foreach (var silo in earlierVersionSilos.Keys)
        {
            if (silos.TryGetValue(silo, out var siloManifest) && siloManifest.Interfaces.TryGetValue(migrationInterface, out var properties))
            {
                var earlierProperties = new GrainInterfaceProperties(properties.Properties.Remove(WellKnownGrainInterfaceProperties.Version));
                silos = silos.SetItem(silo, new GrainManifest(siloManifest.Grains, siloManifest.Interfaces.SetItem(migrationInterface, earlierProperties)));
            }
        }

        return new ClusterManifest(manifest.Version, silos);
    }
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
public class RetirementHandoffGrain : Grain, IRetirementHandoffGrain
{
    private RetirementHandoffState State => RetirementHandoffState.Get(this.GetPrimaryKey());

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        if (State.FailActivationOn is { } failingSilo && failingSilo.Equals(GrainContext.Address.SiloAddress))
        {
            throw new InvalidOperationException("Destination activation failed.");
        }

        if (State.HoldActivationOn is { } silo && silo.Equals(GrainContext.Address.SiloAddress))
        {
            State.ActivationHeld.TrySetResult();
            await State.ReleaseActivation.Task;
        }
    }

    public ValueTask<SiloAddress> GetLocation() => new(GrainContext.Address.SiloAddress!);

    public ValueTask<SiloAddress> GetLocationWithPayload(byte[] payload) => new(GrainContext.Address.SiloAddress!);

    public ValueTask<byte[]> EchoPooledPayload(Orleans.Serialization.Buffers.PooledBuffer payload)
    {
        var result = payload.ToArray();
        payload.Dispose();
        return new(result);
    }

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
    public SiloAddress? FailActivationOn { get; set; }
    public int HoldCount;

    public static RetirementHandoffState Create(Guid key) => States[key] = new();

    public static RetirementHandoffState Get(Guid key) => States[key];
}
