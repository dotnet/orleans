using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Xunit;

namespace UnitTests.ActivationsLifeCycleTests;

public sealed partial class RetirementHandoffTests
{
    [Fact]
    public async Task RequestMigratedTwice_KeepsItsFirstCacheUpdate()
    {
        await using var cluster = await DeployAsync();
        var caller = cluster.Silos[0];
        var first = cluster.Silos[1];
        var second = cluster.Silos[2];
        var locator = caller.ServiceProvider.GetRequiredService<Orleans.Runtime.GrainDirectory.GrainLocator>();
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grain = GetGrain(caller.ServiceProvider.GetRequiredService<IGrainFactory>(), key);
        await PlaceOnAsync(first, () => grain.GetLocation());
        var running = grain.Block().AsTask();
        await state.BlockEntered.Task.WaitAsync(Timeout, Cancellation);
        var hold = grain.HoldThenGetLocation().AsTask();
        await WaitUntilAsync(() => GetWaitingCount(first, grain.GetGrainId()) == 1);
        var queued = grain.Record("queued").AsTask();
        await WaitUntilAsync(() => GetWaitingCount(first, grain.GetGrainId()) == 2);
        try
        {
            await grain.MigrateOnIdleTo(second.SiloAddress);
            state.ReleaseBlock.TrySetResult();
            await running.WaitAsync(Timeout, Cancellation);
            Assert.Equal(second.SiloAddress, await state.HoldEntered.Task.WaitAsync(Timeout, Cancellation));
            await WaitUntilAsync(() => GetWaitingCount(second, grain.GetGrainId()) == 1);
            state.HoldActivationOn = first.SiloAddress;
            await grain.MigrateOnIdleTo(first.SiloAddress);
            state.ReleaseHold.TrySetResult();
            Assert.Equal(second.SiloAddress, await hold.WaitAsync(Timeout, Cancellation));
            Assert.True(locator.TryLookupInCache(grain.GetGrainId(), out var cached));
            Assert.Equal(second.SiloAddress, cached.SiloAddress);
            locator.InvalidateCache(grain.GetGrainId());
            await state.ActivationHeld.Task.WaitAsync(Timeout, Cancellation);
        }
        finally
        {
            state.ReleaseBlock.TrySetResult();
            state.ReleaseHold.TrySetResult();
            state.ReleaseActivation.TrySetResult();
        }
        await queued.WaitAsync(Timeout, Cancellation);
        Assert.True(locator.TryLookupInCache(grain.GetGrainId(), out var result));
        Assert.Equal(second.SiloAddress, result.SiloAddress);
        Assert.Equal(["queued"], state.Calls);
    }

    [Fact]
    public async Task MigrationRoutes_DoNotOverwriteALaterRetarget()
    {
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) => silo.AddIncomingGrainCallFilter(async context =>
        {
            if (context.Request.GetMethodName() == nameof(IActivationMigrationManagerSystemTarget.AcceptMigratingGrains))
            {
                Assert.Single(UnpackMigration(context).Requests!);
                await context.Invoke();
                accepted.TrySetResult();
                await release.Task;
            }
            else await context.Invoke();
        })));
        var source = cluster.Silos[1];
        var factory = cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>();
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grain = GetGrain(factory, key);
        var sender = factory.GetGrain<IRetirementHandoffCallerGrain>(Guid.NewGuid());
        await PlaceOnAsync(source, () => grain.GetLocation());
        await PlaceOnAsync(source, () => sender.GetLocation());
        var running = grain.Block().AsTask();
        await state.BlockEntered.Task.WaitAsync(Timeout, Cancellation);
        var call = sender.CallHoldThenGetLocation(key).AsTask();
        await WaitUntilAsync(() => GetWaitingCount(source, grain.GetGrainId()) == 1);
        var stopping = cluster.StopSiloAsync(source, Cancellation);
        CallbackData callback;
        SiloAddress later;
        try
        {
            await WaitUntilAsync(() => FindActivation(source, grain.GetGrainId()) is ActivationData { State: ActivationState.Deactivating });
            state.ReleaseBlock.TrySetResult();
            await running.WaitAsync(Timeout, Cancellation);
            await accepted.Task.WaitAsync(Timeout, Cancellation);
            var destination = await state.HoldEntered.Task.WaitAsync(Timeout, Cancellation);
            later = cluster.Silos.Select(silo => silo.SiloAddress).First(silo => !silo.Equals(source.SiloAddress) && !silo.Equals(destination));
            callback = FindPendingCallback(source, grain.GetGrainId());
            source.ServiceProvider.GetRequiredService<InsideRuntimeClient>().RetargetCallback(callback.Message.SendingGrain, callback.Message.Id, later, 2);
            release.TrySetResult();
            await WaitUntilAsync(() => FindActivation(source, grain.GetGrainId()) is null);
            Assert.Equal(later, callback.Message.TargetSilo);
            Assert.Equal(2, callback.Message.ForwardCount);
        }
        finally
        {
            release.TrySetResult();
            state.ReleaseBlock.TrySetResult();
            state.ReleaseHold.TrySetResult();
        }
        await call.WaitAsync(Timeout, Cancellation);
        await stopping.WaitAsync(Timeout, Cancellation);
        Assert.Equal(1, state.HoldCount);
    }
}
