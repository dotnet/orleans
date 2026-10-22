using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Diagnostics;
using Orleans.Runtime.Scheduler;
using Orleans.Serialization.Session;
using Orleans.TestingHost.Diagnostics;
using Xunit;

namespace UnitTests.ActivationsLifeCycleTests;

public sealed partial class RetirementHandoffTests
{
    [Fact]
    public async Task MigratedRequests_RunBeforeRequestsWhichReachTheNewActivationDirectly()
    {
        await using var cluster = await DeployAsync();
        var source = cluster.Silos[1];
        var target = cluster.Silos[2];
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grain = GetGrain(cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>(), key);
        await PlaceOnAsync(source, () => grain.GetLocation());
        var running = grain.Block().AsTask();
        await state.BlockEntered.Task.WaitAsync(Timeout, Cancellation);
        var queued = grain.Record("queued").AsTask();
        await WaitUntilAsync(() => GetWaitingCount(source, grain.GetGrainId()) == 1);
        state.HoldActivationOn = target.SiloAddress;
        Task direct;
        try
        {
            await grain.MigrateOnIdleTo(target.SiloAddress);
            state.ReleaseBlock.TrySetResult();
            await running.WaitAsync(Timeout, Cancellation);
            await state.ActivationHeld.Task.WaitAsync(Timeout, Cancellation);
            direct = GetGrain(target.ServiceProvider.GetRequiredService<IGrainFactory>(), key).Record("direct").AsTask();
            await WaitUntilAsync(() => GetWaitingCount(target, grain.GetGrainId()) == 2);
        }
        finally
        {
            state.ReleaseBlock.TrySetResult();
            state.ReleaseActivation.TrySetResult();
        }
        await Task.WhenAll(queued, direct).WaitAsync(Timeout, Cancellation);
        Assert.Equal(["queued", "direct"], state.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MigratedRequests_UseOrdinaryAdmissionForAnExistingActivation(bool deactivating)
    {
        var arrived = new TaskCompletionSource<GrainMigrationPackage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) => silo.AddIncomingGrainCallFilter(async context =>
        {
            if (context.Request.GetMethodName() == nameof(IActivationMigrationManagerSystemTarget.AcceptMigratingGrains))
            {
                arrived.TrySetResult(UnpackMigration(context));
                await release.Task;
            }
            await context.Invoke();
        })));
        var caller = cluster.Silos[0];
        var source = cluster.Silos[1];
        var target = cluster.Silos[2];
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grain = GetGrain(caller.ServiceProvider.GetRequiredService<IGrainFactory>(), key);
        await PlaceOnAsync(source, () => grain.GetLocation());
        var running = grain.Block().AsTask();
        await state.BlockEntered.Task.WaitAsync(Timeout, Cancellation);
        Task migrated;
        RequestContext.Set(Orleans.Runtime.Placement.IPlacementDirector.PlacementHintKey, caller.SiloAddress);
        try { migrated = grain.Record("migrated").AsTask(); }
        finally { RequestContext.Remove(Orleans.Runtime.Placement.IPlacementDirector.PlacementHintKey); }
        await WaitUntilAsync(() => GetWaitingCount(source, grain.GetGrainId()) == 1);
        state.HoldActivationOn = target.SiloAddress;
        try
        {
            await grain.MigrateOnIdleTo(target.SiloAddress);
            state.ReleaseBlock.TrySetResult();
            await running.WaitAsync(Timeout, Cancellation);
            var package = await arrived.Task.WaitAsync(Timeout, Cancellation);
            Assert.True(package.MigrationContext.TryGetValue(ActivationData.GrainAddressMigrationContextKey, out GrainAddress? previous));
            var context = new MigrationContext(target.ServiceProvider.GetRequiredService<SerializerSessionPool>());
            Assert.True(context.TryAddValue(ActivationData.GrainAddressMigrationContextKey, previous));
            var activation = Assert.IsType<ActivationData>(target.ServiceProvider.GetRequiredService<Catalog>()
                .GetOrCreateActivation(package.GrainId, null, context));
            await state.ActivationHeld.Task.WaitAsync(Timeout, Cancellation);
            var local = GetGrain(target.ServiceProvider.GetRequiredService<IGrainFactory>(), key);
            var direct = local.Record("direct").AsTask();
            await WaitUntilAsync(() => activation.WaitingCount == 1);
            if (deactivating)
            {
                state.ReleaseActivation.TrySetResult();
                await direct.WaitAsync(Timeout, Cancellation);
                var holding = local.HoldThenGetLocation().AsTask();
                await state.HoldEntered.Task.WaitAsync(Timeout, Cancellation);
                activation.Deactivate(new(DeactivationReasonCode.ActivationIdle, "Test ordinary admission during deactivation"), Cancellation);
                Assert.Equal(ActivationState.Deactivating, activation.State);
                release.TrySetResult();
                await WaitUntilAsync(() => activation.WaitingCount == 1);
                state.ReleaseHold.TrySetResult();
                await holding.WaitAsync(Timeout, Cancellation);
                await activation.Deactivated.WaitAsync(Timeout, Cancellation);
            }
            else
            {
                release.TrySetResult();
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
            release.TrySetResult();
            state.ReleaseBlock.TrySetResult();
            state.ReleaseActivation.TrySetResult();
            state.ReleaseHold.TrySetResult();
        }
    }

    [Fact]
    public async Task MigratedRequests_UseOrdinaryPerRequestOverloadAdmission()
    {
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) =>
            silo.Configure<SiloMessagingOptions>(options => options.MaxEnqueuedRequestsHardLimit = 1)));
        var silo = cluster.Silos[1];
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grainId = GetGrain(cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>(), key).GetGrainId();
        state.HoldActivationOn = silo.SiloAddress;
        var package = new GrainMigrationPackage
        {
            GrainId = grainId,
            MigrationContext = new MigrationContext(silo.ServiceProvider.GetRequiredService<SerializerSessionPool>()),
            Requests = [CreateOneWayRequest(grainId, 1), CreateOneWayRequest(grainId, 2), CreateOneWayRequest(grainId, 3)],
        };
        var accepting = silo.ServiceProvider.GetRequiredService<ActivationMigrationManager>().AcceptMigratingGrains([package], Cancellation).AsTask();
        try
        {
            await state.ActivationHeld.Task.WaitAsync(Timeout, Cancellation);
            Assert.Equal(2, Assert.IsType<ActivationData>(FindActivation(silo, grainId)).WaitingCount);
            Assert.Empty(package.Requests!);
        }
        finally { state.ReleaseActivation.TrySetResult(); }
        await accepting.WaitAsync(Timeout, Cancellation);
    }

    [Fact]
    public async Task MigratedRequests_AreRejectedWhenDestinationActivationFails()
    {
        await using var cluster = await DeployAsync();
        var source = cluster.Silos[1];
        var target = cluster.Silos[2];
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grain = GetGrain(cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>(), key);
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
        finally { state.ReleaseBlock.TrySetResult(); }
    }

    [Fact]
    public async Task CancellationReachingTheActivatingNewActivation_CancelsTheMigratedRequestBeforeItRuns()
    {
        await using var cluster = await DeployAsync();
        var caller = cluster.Silos[0];
        var source = cluster.Silos[1];
        var target = cluster.Silos[2];
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grain = GetGrain(caller.ServiceProvider.GetRequiredService<IGrainFactory>(), key);
        await PlaceOnAsync(source, () => grain.GetLocation());
        var running = grain.Block().AsTask();
        await state.BlockEntered.Task.WaitAsync(Timeout, Cancellation);
        var call = grain.HoldUntilCanceled(CancellationToken.None).AsTask();
        await WaitUntilAsync(() => GetWaitingCount(source, grain.GetGrainId()) == 1);
        var pending = FindPendingCallback(caller, grain.GetGrainId(), nameof(IRetirementHandoffGrain.HoldUntilCanceled)).Message;
        state.HoldActivationOn = target.SiloAddress;
        try
        {
            await grain.MigrateOnIdleTo(target.SiloAddress);
            state.ReleaseBlock.TrySetResult();
            await running.WaitAsync(Timeout, Cancellation);
            await state.ActivationHeld.Task.WaitAsync(Timeout, Cancellation);
            var activation = Assert.IsType<ActivationData>(FindActivation(target, grain.GetGrainId()));
            Assert.Equal(1, activation.WaitingCount);
            await ((IGrainCallCancellationExtension)activation).CancelRequestAsync(pending.SendingGrain, pending.Id, CancellationToken.None)
                .AsTask().WaitAsync(Timeout, Cancellation);
            Assert.Equal(0, activation.WaitingCount);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(Timeout, Cancellation));
            state.ReleaseActivation.TrySetResult();
            await WaitUntilAsync(() => activation.State is ActivationState.Valid);
            await activation.RunOrQueueTask(() => Task.CompletedTask);
            Assert.False(state.HoldEntered.Task.IsCompleted);
            Assert.Equal(0, activation.GetRequestCount());
        }
        finally
        {
            state.ReleaseBlock.TrySetResult();
            state.ReleaseActivation.TrySetResult();
            state.ReleaseHold.TrySetResult();
        }
    }

    [Fact]
    public async Task QueuedRequestWithExhaustedForwards_IsRejectedInsteadOfHandedOff()
    {
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) =>
            silo.Configure<SiloMessagingOptions>(options => options.MaxForwardCount = 0)));
        var source = cluster.Silos[1];
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grain = GetGrain(cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>(), key);
        await PlaceOnAsync(source, () => grain.GetLocation());
        var running = grain.Block().AsTask();
        await state.BlockEntered.Task.WaitAsync(Timeout, Cancellation);
        var queued = grain.HoldThenGetLocation().AsTask();
        await WaitUntilAsync(() => GetWaitingCount(source, grain.GetGrainId()) == 1);
        try
        {
            await grain.MigrateOnIdleTo(cluster.Silos[2].SiloAddress);
            state.ReleaseBlock.TrySetResult();
            await running.WaitAsync(Timeout, Cancellation);
            await Assert.ThrowsAnyAsync<OrleansMessageRejectionException>(() => queued.WaitAsync(Timeout, Cancellation));
            Assert.Equal(0, state.HoldCount);
        }
        finally { state.ReleaseBlock.TrySetResult(); state.ReleaseHold.TrySetResult(); }
    }

    [Fact]
    public async Task MigratedRequestWhichExpiredInTransit_IsDroppedAndTheOthersAreAccepted()
    {
        var received = new TaskCompletionSource<(Message Expired, Message Live)>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) => silo.AddIncomingGrainCallFilter(async context =>
        {
            if (context.Request.GetMethodName() == nameof(IActivationMigrationManagerSystemTarget.AcceptMigratingGrains))
            {
                var requests = Assert.IsType<List<Message>>(UnpackMigration(context).Requests);
                Assert.Equal(2, requests.Count);
                requests[0].TimeToLive = TimeSpan.FromSeconds(-1);
                received.TrySetResult((requests[0], requests[1]));
            }
            await context.Invoke();
        })));
        var caller = cluster.Silos[0];
        var source = cluster.Silos[1];
        var target = cluster.Silos[2];
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grain = GetGrain(caller.ServiceProvider.GetRequiredService<IGrainFactory>(), key);
        await PlaceOnAsync(source, () => grain.GetLocation());
        var running = grain.Block().AsTask();
        await state.BlockEntered.Task.WaitAsync(Timeout, Cancellation);
        var expired = grain.Record("expired").AsTask();
        await WaitUntilAsync(() => GetWaitingCount(source, grain.GetGrainId()) == 1);
        var callback = FindPendingCallback(caller, grain.GetGrainId(), nameof(IRetirementHandoffGrain.Record));
        var live = grain.Record("live").AsTask();
        await WaitUntilAsync(() => GetWaitingCount(source, grain.GetGrainId()) == 2);
        try
        {
            await grain.MigrateOnIdleTo(target.SiloAddress);
            state.ReleaseBlock.TrySetResult();
            await running.WaitAsync(Timeout, Cancellation);
            var requests = await received.Task.WaitAsync(Timeout, Cancellation);
            callback.OnTimeout();
            await Assert.ThrowsAsync<TimeoutException>(() => expired.WaitAsync(Timeout, Cancellation));
            await live.WaitAsync(Timeout, Cancellation);
            Assert.Equal(0, requests.Expired.ForwardCount);
            Assert.Equal(1, requests.Live.ForwardCount);
            Assert.Equal(["live"], state.Calls);
            Assert.Equal(target.SiloAddress, await grain.GetLocation().AsTask().WaitAsync(Timeout, Cancellation));
        }
        finally { state.ReleaseBlock.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Catalog_CallsActivateAndTheActivationOwnsAdmittedRequests(bool rejectResponseFails)
    {
        var control = new MigrationAdmissionControl();
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) =>
        {
            silo.Services.AddSingleton<IConfigureGrainContextProvider>(control);
            if (rejectResponseFails) silo.Configure<SiloMessagingOptions>(options => options.MaxEnqueuedRequestsHardLimit = 1);
        }));
        var silo = cluster.Silos[1];
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        state.FailActivationOn = silo.SiloAddress;
        var grain = GetGrain(cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>(), key);
        var grainId = grain.GetGrainId();
        ActivationData? activation = null;
        control.OnCreate = context => { if (context.GrainId == grainId) activation = Assert.IsType<ActivationData>(context); };
        var messages = Enumerable.Range(0, 3).Select(id => CreateRequest(cluster, grainId, id, new byte[20_000])).ToList();
        using var migrationContext = new MigrationContext(silo.ServiceProvider.GetRequiredService<SerializerSessionPool>());
        var package = new GrainMigrationPackage { GrainId = grainId, MigrationContext = migrationContext, Requests = messages };
        using var events = new DiagnosticEventCollector(GrainLifecycleEvents.ListenerName);
        var ownedCount = -1;
        var deactivating = events.WaitForEventAsync(nameof(GrainLifecycleEvents.Deactivating), e =>
        {
            if (e.Payload is GrainLifecycleEvents.Deactivating transition && ReferenceEquals(transition.GrainContext, activation))
            {
                ownedCount = activation!.WaitingCount;
                return true;
            }
            return false;
        }, Timeout, Cancellation);
        try
        {
            try
            {
                if (rejectResponseFails) RequestContext.Set("uncopyable-rejection-context", new UnserializableValue());
                var error = Record.Exception(() => silo.ServiceProvider.GetRequiredService<Catalog>()
                    .GetOrCreateActivation(grainId, null, package.MigrationContext, package.Requests));
                if (rejectResponseFails) Assert.NotNull(error);
                else Assert.Null(error);
            }
            finally { RequestContext.Remove("uncopyable-rejection-context"); }
            var reason = Assert.IsType<GrainLifecycleEvents.Deactivating>((await deactivating).Payload).Reason;
            Assert.Equal(DeactivationReasonCode.ActivationFailed, reason.ReasonCode);
            Assert.Equal("Destination activation failed.", Assert.IsType<InvalidOperationException>(reason.Exception).Message);
            Assert.Equal(rejectResponseFails ? 2 : 3, ownedCount);
            await activation!.Deactivated.WaitAsync(Timeout, Cancellation);
            Assert.Equal(ActivationState.Invalid, activation.State);
            Assert.Equal(0, activation.WaitingCount);
            Assert.Null(FindActivation(silo, grainId));
            Assert.Empty(package.Requests!);
            Assert.Empty(migrationContext.Keys);
            control.OnCreate = null;
            state.FailActivationOn = null;
            await PlaceOnAsync(silo, () => grain.GetLocation());
            Assert.NotSame(activation, FindActivation(silo, grainId));
        }
        finally
        {
            control.OnCreate = null;
            if (activation is not null)
            {
                activation.Deactivate(new(DeactivationReasonCode.ActivationFailed, new InvalidOperationException("Test cleanup"), "Test cleanup"), CancellationToken.None);
                await activation.Deactivated.WaitAsync(Timeout, Cancellation);
            }
        }
    }

    [Theory]
    [InlineData("target")]
    [InlineData("local")]
    [InlineData("response")]
    [InlineData("canceled")]
    public async Task InvalidMigrationBatch_RejectsBeforeAdmittingAnyPackage(string invalid)
    {
        await using var cluster = await DeployAsync();
        var destination = cluster.Silos[1];
        var factory = cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>();
        var firstKey = Guid.NewGuid();
        var secondKey = Guid.NewGuid();
        RetirementHandoffState.Create(firstKey);
        RetirementHandoffState.Create(secondKey);
        var firstId = GetGrain(factory, firstKey).GetGrainId();
        var secondId = GetGrain(factory, secondKey).GetGrainId();
        using var firstContext = new MigrationContext(destination.ServiceProvider.GetRequiredService<SerializerSessionPool>());
        using var secondContext = new MigrationContext(destination.ServiceProvider.GetRequiredService<SerializerSessionPool>());
        Assert.True(firstContext.TryAddValue("first", 41));
        Assert.True(secondContext.TryAddValue("second", 42));
        var invalidRequest = CreateOneWayRequest(secondId, 2);
        if (invalid == "target") invalidRequest.TargetGrain = firstId;
        else if (invalid == "local") invalidRequest.IsLocalOnly = true;
        else if (invalid == "response") invalidRequest.Direction = Message.Directions.Response;
        List<Message> firstRequests = [CreateOneWayRequest(firstId, 1)];
        List<Message> secondRequests = [invalidRequest];
        List<GrainMigrationPackage> packages =
        [
            new() { GrainId = firstId, MigrationContext = firstContext, Requests = firstRequests },
            new() { GrainId = secondId, MigrationContext = secondContext, Requests = secondRequests },
        ];
        using var cancellation = new CancellationTokenSource();
        if (invalid == "canceled") cancellation.Cancel();
        var accepting = destination.ServiceProvider.GetRequiredService<ActivationMigrationManager>().AcceptMigratingGrains(packages, cancellation.Token).AsTask();
        if (invalid == "canceled") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => accepting);
        else await Assert.ThrowsAsync<ArgumentException>(() => accepting);
        Assert.Null(FindActivation(destination, firstId));
        Assert.Null(FindActivation(destination, secondId));
        Assert.Empty(firstContext.Keys);
        Assert.Empty(secondContext.Keys);
        Assert.Empty(firstRequests);
        Assert.Empty(secondRequests);
    }

    private sealed class UnserializableValue;
}
