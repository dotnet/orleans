using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Scheduler;
using Orleans.Serialization;
using Orleans.Serialization.Session;
using Xunit;

namespace UnitTests.ActivationsLifeCycleTests;

public sealed partial class RetirementHandoffTests
{
    [Fact]
    public async Task SentMigration_CancellationWaitsForItsOutcome()
    {
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        using var context = new MigrationContext(services.GetRequiredService<SerializerSessionPool>());
        using var cancellation = new CancellationTokenSource();
        var migration = CreateMigration(context, cancellation.Token);
        migration.RegisterCancellation();
        Assert.True(migration.TrySend());
        cancellation.Cancel();
        Assert.False(migration.Completion.IsCompleted);
        var failure = new InvalidOperationException("Acceptance outcome unknown.");
        migration.Complete(failure);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => migration.Completion));
        Assert.False(migration.TrySend());
    }

    [Fact]
    public async Task AcceptedMigration_CancellationCompletesHeldRetargetsSuccessfully()
    {
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        using var context = new MigrationContext(services.GetRequiredService<SerializerSessionPool>());
        using var cancellation = new CancellationTokenSource();
        var migration = CreateMigration(context, cancellation.Token);
        migration.RegisterCancellation();
        Assert.True(migration.TrySend());
        migration.SetAccepted();
        Assert.False(migration.Completion.IsCompleted);
        cancellation.Cancel();
        await migration.Completion.WaitAsync(Timeout, Cancellation);
        Assert.True(migration.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task MigrationWithoutRequests_CompletesWithoutWaitingForAnotherGrainsRetargets()
    {
        var retargetArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) => silo.AddIncomingGrainCallFilter(async context =>
        {
            if (context.Request.GetMethodName() == nameof(IActivationMigrationManagerSystemTarget.RetargetMigratedRequests))
            {
                retargetArrived.TrySetResult();
                await release.Task;
            }
            await context.Invoke();
        })));
        var source = cluster.Silos[1];
        var target = cluster.Silos[2];
        var manager = source.ServiceProvider.GetRequiredService<ActivationMigrationManager>();
        var pool = source.ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var factory = cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>();
        var firstKey = Guid.NewGuid();
        var secondKey = Guid.NewGuid();
        RetirementHandoffState.Create(firstKey);
        RetirementHandoffState.Create(secondKey);
        var firstId = GetGrain(factory, firstKey).GetGrainId();
        var secondId = GetGrain(factory, secondKey).GetGrainId();
        ActivationMigration? first = null;
        ActivationMigration? second = null;
        await manager.RunOrQueueTask(() =>
        {
            first = manager.StartMigration(target.SiloAddress, CreateSourceAddress(source, firstId), new MigrationContext(pool),
                [CreateRequest(cluster, firstId, 1, new byte[1])], Cancellation);
            second = manager.StartMigration(target.SiloAddress, CreateSourceAddress(source, secondId), new MigrationContext(pool), null, Cancellation);
            return Task.CompletedTask;
        });
        try
        {
            await retargetArrived.Task.WaitAsync(Timeout, Cancellation);
            await second!.Completion.WaitAsync(Timeout, Cancellation);
            Assert.True(second.Completion.IsCompletedSuccessfully);
            Assert.False(first!.Completion.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await first!.Completion.WaitAsync(Timeout, Cancellation);
    }

    [Fact]
    public async Task MigrationAcceptance_HeldOutgoingFilterTimesOutAndAllowsDeactivation()
    {
        var timeout = TimeSpan.FromSeconds(10);
        var entered = new TaskCompletionSource<GrainMigrationPackage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepted = 0;
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) =>
        {
            silo.Services.AddKeyedSingleton<TimeProvider>(TimeProviderNames.Messaging, new FakeTimeProvider());
            silo.Configure<SiloMessagingOptions>(options => options.SystemResponseTimeout = timeout);
            silo.AddOutgoingGrainCallFilter(async context =>
            {
                if (context.Request.GetMethodName() != nameof(IActivationMigrationManagerSystemTarget.AcceptMigratingGrains))
                {
                    await context.Invoke();
                    return;
                }
                entered.TrySetResult(UnpackMigration(context));
                await release.Task;
                try { await context.Invoke(); finished.TrySetResult(null); }
                catch (Exception error) { finished.TrySetResult(error); throw; }
            });
            silo.AddIncomingGrainCallFilter(async context =>
            {
                if (context.Request.GetMethodName() == nameof(IActivationMigrationManagerSystemTarget.AcceptMigratingGrains)) Interlocked.Increment(ref accepted);
                await context.Invoke();
            });
        }));
        var source = cluster.Silos[1];
        var target = cluster.Silos[2];
        var manager = source.ServiceProvider.GetRequiredService<ActivationMigrationManager>();
        var time = Assert.IsType<FakeTimeProvider>(source.ServiceProvider.GetRequiredKeyedService<TimeProvider>(TimeProviderNames.Messaging));
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grain = GetGrain(cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>(), key);
        await PlaceOnAsync(source, () => grain.GetLocation());
        var activation = Assert.IsType<ActivationData>(FindActivation(source, grain.GetGrainId()));
        var running = grain.Block().AsTask();
        await state.BlockEntered.Task.WaitAsync(Timeout, Cancellation);
        var queued = grain.Record("queued").AsTask();
        await WaitUntilAsync(() => activation.WaitingCount == 1);
        try
        {
            await grain.MigrateOnIdleTo(target.SiloAddress);
            state.ReleaseBlock.TrySetResult();
            await running.WaitAsync(Timeout, Cancellation);
            var package = await entered.Task.WaitAsync(Timeout, Cancellation);
            await manager.RunOrQueueTask(() => Task.CompletedTask);
            time.Advance(timeout - TimeSpan.FromTicks(1));
            await manager.RunOrQueueTask(() => Task.CompletedTask);
            Assert.False(package.Source!.Completion.IsCompleted);
            Assert.False(activation.Deactivated.IsCompleted);
            time.Advance(TimeSpan.FromTicks(1));
            await activation.Deactivated.WaitAsync(Timeout, Cancellation);
            await Assert.ThrowsAsync<TimeoutException>(() => package.Source.Completion);
            await Assert.ThrowsAsync<OrleansMessageRejectionException>(() => queued.WaitAsync(Timeout, Cancellation));
            Assert.Null(FindActivation(source, grain.GetGrainId()));
            Assert.Empty(state.Calls);
        }
        finally
        {
            state.ReleaseBlock.TrySetResult();
            release.TrySetResult();
            if (entered.Task.IsCompletedSuccessfully) await finished.Task.WaitAsync(Timeout, Cancellation);
        }
        Assert.IsAssignableFrom<OperationCanceledException>(await finished.Task);
        Assert.Equal(0, Volatile.Read(ref accepted));
        Assert.Null(FindActivation(target, grain.GetGrainId()));
        Assert.Empty(state.Calls);
    }

    [Fact]
    public async Task PreparationFailure_RejectsRequestsWithoutReplay()
    {
        await using var cluster = await DeployAsync();
        var source = cluster.Silos[1];
        var target = cluster.Silos[2];
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grain = GetGrain(source.ServiceProvider.GetRequiredService<IGrainFactory>(), key);
        await PlaceOnAsync(source, () => grain.GetLocation());
        var activation = Assert.IsType<ActivationData>(FindActivation(source, grain.GetGrainId()));
        var running = grain.Block().AsTask();
        await state.BlockEntered.Task.WaitAsync(Timeout, Cancellation);
        var queued = grain.Record("must-not-run").AsTask();
        await WaitUntilAsync(() => activation.WaitingCount == 1);
        // Local dispatch shares this eager message with the activation's queue.
        FindPendingCallback(source, grain.GetGrainId(), nameof(IRetirementHandoffGrain.Record)).Message.RequestContextData =
            new() { ["invalid"] = new UnserializableValue() };
        try
        {
            activation.Migrate(new() { [Orleans.Runtime.Placement.IPlacementDirector.PlacementHintKey] = target.SiloAddress }, Cancellation);
            state.ReleaseBlock.TrySetResult();
            await running.WaitAsync(Timeout, Cancellation);
            await Assert.ThrowsAsync<OrleansMessageRejectionException>(() => queued.WaitAsync(Timeout, Cancellation));
            await activation.Deactivated.WaitAsync(Timeout, Cancellation);
            Assert.Empty(state.Calls);
            Assert.Equal(0, activation.GetRequestCount());
            Assert.Null(FindActivation(target, grain.GetGrainId()));
        }
        finally { state.ReleaseBlock.TrySetResult(); }
    }

    private static ActivationMigration CreateMigration(MigrationContext context, CancellationToken cancellation)
    {
        var source = SiloAddress.New(IPAddress.Loopback, 10001, 1);
        var target = SiloAddress.New(IPAddress.Loopback, 10002, 1);
        var grainId = GrainId.Create("test", "migration");
        return new ActivationMigration(null!, new GrainAddress { SiloAddress = source, GrainId = grainId, ActivationId = ActivationId.NewId() },
            target, new GrainMigrationPackage { GrainId = grainId, MigrationContext = context }, [],
            [(source, new MigratedRequestRoute(GrainId.Create("test", "caller"), new CorrelationId(1), target, 1))], 0, cancellation);
    }
}
