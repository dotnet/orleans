using Microsoft.Extensions.DependencyInjection;
using Orleans.Concurrency;
using Orleans.Configuration;
using Orleans.Placement;
using Orleans.Placement.Repartitioning;
using Orleans.Runtime;
using Orleans.Runtime.Diagnostics;
using Orleans.Runtime.Placement;
using Orleans.TestingHost.Diagnostics;
using Xunit;

namespace UnitTests.ActivationsLifeCycleTests;

public sealed partial class RetirementHandoffTests
{
    [Fact]
    public async Task MigratabilityChecker_ExcludesImmovableStatelessAndUnknownGrains()
    {
        await using var cluster = await DeployAsync();
        var services = cluster.Silos[1].ServiceProvider;
        var checker = services.GetRequiredService<GrainMigratabilityChecker>();
        var factory = services.GetRequiredService<IGrainFactory>();
        Assert.True(checker.IsMigratable(GetGrain(factory, Guid.NewGuid()).GetGrainId().Type, ImmovableKind.Any));
        Assert.False(checker.IsMigratable(factory.GetGrain<IImmovableRetirementHandoffGrain>(Guid.NewGuid()).GetGrainId().Type, ImmovableKind.Any));
        Assert.False(checker.IsMigratable(factory.GetGrain<IStatelessRetirementHandoffGrain>(Guid.NewGuid()).GetGrainId().Type, ImmovableKind.Any));
        Assert.False(checker.IsMigratable(GrainType.Create("unknown-grain-type"), ImmovableKind.Any));
    }

    [Fact]
    public async Task QueuedRequestForImmovableGrain_FailsWhenRetiringSiloDeparts()
    {
        var migrations = 0;
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) => silo.AddIncomingGrainCallFilter(async context =>
        {
            if (context.Request.GetMethodName() == nameof(IActivationMigrationManagerSystemTarget.AcceptMigratingGrains))
                Interlocked.Increment(ref migrations);
            await context.Invoke();
        })));
        var retiring = cluster.Silos[1];
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grain = cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>().GetGrain<IImmovableRetirementHandoffGrain>(key);
        await PlaceOnAsync(retiring, () => grain.GetLocation());
        var running = grain.Block().AsTask();
        await state.BlockEntered.Task.WaitAsync(Timeout, Cancellation);
        var queued = grain.HoldThenGetLocation().AsTask();
        await WaitUntilAsync(() => GetWaitingCount(retiring, grain.GetGrainId()) == 1);
        var callback = FindPendingCallback(cluster.Silos[0], grain.GetGrainId(), nameof(IRetirementHandoffGrain.HoldThenGetLocation));
        var stopping = cluster.StopSiloAsync(retiring, Cancellation);
        try
        {
            await WaitUntilAsync(() => FindActivation(retiring, grain.GetGrainId()) is ActivationData { State: ActivationState.Deactivating });
            state.ReleaseBlock.TrySetResult();
            await running.WaitAsync(Timeout, Cancellation);
            await stopping.WaitAsync(Timeout, Cancellation);
            // The old eager transport can reject a forwarded request as "Silo stopping" before the caller's
            // membership sweep observes departure. Both are prompt terminal failures, not a retarget.
            var error = await Record.ExceptionAsync(() => queued.WaitAsync(Timeout, Cancellation));
            if (error is SiloUnavailableException) Assert.IsType<SiloUnavailableException>(error);
            else Assert.Equal("Silo stopping ", Assert.IsType<OrleansMessageRejectionException>(error).Message);
            Assert.Equal(retiring.SiloAddress, callback.Message.TargetSilo);
            Assert.Equal(0, callback.Message.ForwardCount);
            Assert.Equal(0, Volatile.Read(ref migrations));
        }
        finally { state.ReleaseBlock.TrySetResult(); state.ReleaseHold.TrySetResult(); }
    }

    [Fact]
    public async Task DeactivationUnderwayWhenRetiring_KeepsActivationInCatalogAndIsAwaited()
    {
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) =>
            silo.Configure<SiloMessagingOptions>(options => options.WaitForMessageToBeQueuedForOutboundTime = TimeSpan.Zero)));
        var retiring = cluster.Silos[1];
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grain = cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>().GetGrain<IDisposalHeldGrain>(key);
        await PlaceOnAsync(retiring, () => grain.GetLocation());
        var activation = Assert.IsType<ActivationData>(FindActivation(retiring, grain.GetGrainId()));
        var directory = retiring.ServiceProvider.GetRequiredService<ActivationDirectory>();
        await grain.DeactivateNow();
        await state.DisposeEntered.Task.WaitAsync(Timeout, Cancellation);
        Assert.Same(activation, FindActivation(retiring, grain.GetGrainId()));
        using var stages = new SiloStageObserver(retiring.SiloAddress, ServiceLifecycleStage.GrainDeactivation, state.ReleaseDispose.Task);
        var stopping = cluster.StopSiloAsync(retiring, Cancellation);
        try
        {
            await stages.Stopping.WaitAsync(Timeout, Cancellation);
            Assert.Same(activation, FindActivation(retiring, grain.GetGrainId()));
            Assert.False(activation.Deactivated.IsCompleted);
            Assert.False(stopping.IsCompleted);
        }
        finally { state.ReleaseDispose.TrySetResult(); }
        Assert.True(await stages.StoppedAfterRelease.WaitAsync(Timeout, Cancellation));
        await stopping.WaitAsync(Timeout, Cancellation);
        Assert.Null(directory.FindTarget(grain.GetGrainId()));
    }

    [Fact]
    public async Task MigrationUnderwayWhenRetiring_CarriesQueuedRequests()
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) => silo.AddIncomingGrainCallFilter(async context =>
        {
            if (context.Request.GetMethodName() == nameof(IActivationMigrationManagerSystemTarget.AcceptMigratingGrains))
            {
                arrived.TrySetResult();
                await release.Task;
            }
            await context.Invoke();
        })));
        var source = cluster.Silos[1];
        var target = cluster.Silos[2];
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grain = GetGrain(cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>(), key);
        await PlaceOnAsync(source, () => grain.GetLocation());
        var running = grain.Block().AsTask();
        await state.BlockEntered.Task.WaitAsync(Timeout, Cancellation);
        var queued = grain.HoldThenGetLocation().AsTask();
        await WaitUntilAsync(() => GetWaitingCount(source, grain.GetGrainId()) == 1);
        Task stopping;
        try
        {
            await grain.MigrateOnIdleTo(target.SiloAddress);
            state.ReleaseBlock.TrySetResult();
            await running.WaitAsync(Timeout, Cancellation);
            await arrived.Task.WaitAsync(Timeout, Cancellation);
            using var stages = new SiloStageObserver(source.SiloAddress, ServiceLifecycleStage.GrainDeactivation, release.Task);
            stopping = cluster.StopSiloAsync(source, Cancellation);
            await stages.Stopping.WaitAsync(Timeout, Cancellation);
            Assert.False(stopping.IsCompleted);
        }
        finally { state.ReleaseBlock.TrySetResult(); release.TrySetResult(); }
        try
        {
            Assert.Equal(target.SiloAddress, await state.HoldEntered.Task.WaitAsync(Timeout, Cancellation));
            await stopping.WaitAsync(Timeout, Cancellation);
            await cluster.WaitForLivenessToStabilizeAsync().WaitAsync(Timeout, Cancellation);
            Assert.False(queued.IsCompleted);
            state.ReleaseHold.TrySetResult();
            Assert.Equal(target.SiloAddress, await queued.WaitAsync(Timeout, Cancellation));
        }
        finally { state.ReleaseHold.TrySetResult(); }
    }

    [Fact]
    public async Task ActivationInterruptedByRetirement_DoesNotMigrate()
    {
        var migrations = 0;
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) => silo.AddIncomingGrainCallFilter(async context =>
        {
            if (context.Request.GetMethodName() == nameof(IActivationMigrationManagerSystemTarget.AcceptMigratingGrains)) Interlocked.Increment(ref migrations);
            await context.Invoke();
        })));
        var retiring = cluster.Silos[1];
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grain = GetGrain(cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>(), key);
        state.HoldActivationOn = retiring.SiloAddress;
        Task call;
        RequestContext.Set(IPlacementDirector.PlacementHintKey, retiring.SiloAddress);
        try { call = grain.Record("queued").AsTask(); }
        finally { RequestContext.Remove(IPlacementDirector.PlacementHintKey); }
        await state.ActivationHeld.Task.WaitAsync(Timeout, Cancellation);
        var activation = Assert.IsType<ActivationData>(FindActivation(retiring, grain.GetGrainId()));
        await WaitUntilAsync(() => activation.WaitingCount == 1);
        var stopping = cluster.StopSiloAsync(retiring, Cancellation);
        try { await WaitUntilAsync(() => activation.State is ActivationState.Deactivating or ActivationState.Invalid); }
        finally { state.ReleaseActivation.TrySetResult(); }
        await stopping.WaitAsync(Timeout, Cancellation);
        await call.WaitAsync(Timeout, Cancellation).ContinueWith(static _ => { }, TaskScheduler.Default);
        Assert.Equal(0, Volatile.Read(ref migrations));
        Assert.Equal(ActivationState.Invalid, activation.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedOneWayRequest_MigratesWithoutRetargetingItsCaller(bool retireSource)
    {
        var retargets = 0;
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) => silo.AddIncomingGrainCallFilter(async context =>
        {
            if (context.Request.GetMethodName() == nameof(IActivationMigrationManagerSystemTarget.RetargetMigratedRequests)) Interlocked.Increment(ref retargets);
            await context.Invoke();
        })));
        var source = cluster.Silos[1];
        var target = cluster.Silos[2];
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grain = GetGrain(cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>(), key);
        await PlaceOnAsync(source, () => grain.GetLocation());
        var running = grain.Block().AsTask();
        await state.BlockEntered.Task.WaitAsync(Timeout, Cancellation);
        await grain.RecordOneWay();
        await WaitUntilAsync(() => GetWaitingCount(source, grain.GetGrainId()) == 1);
        var stopping = Task.CompletedTask;
        try
        {
            if (retireSource)
            {
                stopping = cluster.StopSiloAsync(source, Cancellation);
                await WaitUntilAsync(() => FindActivation(source, grain.GetGrainId()) is ActivationData { State: ActivationState.Deactivating });
            }
            else await grain.MigrateOnIdleTo(target.SiloAddress);
        }
        finally { state.ReleaseBlock.TrySetResult(); }
        await running.WaitAsync(Timeout, Cancellation);
        var destination = await state.OneWayReceived.Task.WaitAsync(Timeout, Cancellation);
        Assert.NotEqual(source.SiloAddress, destination);
        if (!retireSource)
        {
            Assert.Equal(target.SiloAddress, destination);
            Assert.Equal(destination, await grain.GetLocation().AsTask().WaitAsync(Timeout, Cancellation));
        }
        await stopping.WaitAsync(Timeout, Cancellation);
        Assert.Equal(0, Volatile.Read(ref retargets));
    }
}

public interface IImmovableRetirementHandoffGrain : IRetirementHandoffGrain;
[PreferLocalPlacement, Immovable]
public sealed class ImmovableRetirementHandoffGrain : RetirementHandoffGrain, IImmovableRetirementHandoffGrain;
public interface IStatelessRetirementHandoffGrain : IGrainWithGuidKey { ValueTask Ping(); }
[StatelessWorker]
public sealed class StatelessRetirementHandoffGrain : Grain, IStatelessRetirementHandoffGrain { public ValueTask Ping() => default; }
public interface IDisposalHeldGrain : IGrainWithGuidKey
{
    ValueTask<SiloAddress> GetLocation();
    ValueTask DeactivateNow();
}
[PreferLocalPlacement]
public sealed class DisposalHeldGrain : Grain, IDisposalHeldGrain, IAsyncDisposable
{
    public ValueTask<SiloAddress> GetLocation() => new(GrainContext.Address.SiloAddress!);
    public ValueTask DeactivateNow() { DeactivateOnIdle(); return default; }
    public async ValueTask DisposeAsync()
    {
        var state = RetirementHandoffState.Get(this.GetPrimaryKey());
        state.DisposeEntered.TrySetResult();
        await state.ReleaseDispose.Task;
    }
}

internal sealed class SiloStageObserver : IObserver<SiloLifecycleEvents.LifecycleEvent>, IDisposable
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
        _subscription = SiloLifecycleEvents.AllEvents.Subscribe(this);
    }
    public Task Stopping => _stopping.Task;
    public Task<bool> StoppedAfterRelease => _stopped.Task;
    public void OnNext(SiloLifecycleEvents.LifecycleEvent value)
    {
        if (value.Stage != _stage || !_silo.Equals(value.SiloAddress)) return;
        if (value is SiloLifecycleEvents.ObserverStopping) _stopping.TrySetResult();
        else if (value is SiloLifecycleEvents.StageStopped) _stopped.TrySetResult(_released.IsCompleted);
    }
    public void OnCompleted() { }
    public void OnError(Exception error) { }
    public void Dispose() => _subscription.Dispose();
}
