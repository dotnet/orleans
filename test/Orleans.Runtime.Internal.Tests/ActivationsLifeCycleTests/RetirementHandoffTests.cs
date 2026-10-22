using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Concurrency;
using Orleans.Configuration;
using Orleans.Metadata;
using Orleans.Placement;
using Orleans.Placement.Repartitioning;
using Orleans.Runtime;
using Orleans.Runtime.Diagnostics;
using Orleans.Runtime.Placement;
using Orleans.Runtime.Scheduler;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Session;
using Orleans.TestingHost;
using Orleans.TestingHost.Diagnostics;
using TestExtensions;
using Xunit;

namespace UnitTests.ActivationsLifeCycleTests;

[TestSuite("BVT"), TestProvider("None"), TestCategory("BVT")]
public sealed partial class RetirementHandoffTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedRequest_MigratesWithActivation_AndSurvivesRetiringSiloDeparture(bool directoryOwnedByRetiringSilo)
    {
        await using var cluster = await DeployAsync();
        var retiring = cluster.Silos[1];
        var caller = cluster.Silos[0];
        var factory = caller.ServiceProvider.GetRequiredService<IGrainFactory>();
        var directory = caller.ServiceProvider.GetRequiredService<Orleans.Runtime.GrainDirectory.LocalGrainDirectory>();
        Guid key;
        do
        {
            key = Guid.NewGuid();
        }
        while (retiring.SiloAddress.Equals(directory.CalculateGrainDirectoryPartition(GetGrain(factory, key).GetGrainId())) != directoryOwnedByRetiringSilo);
        var state = RetirementHandoffState.Create(key);
        var grain = GetGrain(factory, key);
        await PlaceOnAsync(retiring, () => grain.GetLocation());
        var running = grain.Block().AsTask();
        await state.BlockEntered.Task.WaitAsync(Timeout, Cancellation);
        var queued = grain.HoldThenGetLocation().AsTask();
        await WaitUntilAsync(() => GetWaitingCount(retiring, grain.GetGrainId()) == 1);
        var stopping = cluster.StopSiloAsync(retiring, Cancellation);
        try
        {
            await WaitUntilAsync(() => FindActivation(retiring, grain.GetGrainId()) is ActivationData { State: ActivationState.Deactivating });
            Assert.False(stopping.IsCompleted);
            Assert.False(state.HoldEntered.Task.IsCompleted);
            state.ReleaseBlock.TrySetResult();
            await running.WaitAsync(Timeout, Cancellation);
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
        finally
        {
            state.ReleaseBlock.TrySetResult();
            state.ReleaseHold.TrySetResult();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitMigration_CarriesQueuedRequestsAndUpdatesTheCallersCache(bool migrateDirectly)
    {
        var carried = new TaskCompletionSource<CorrelationId>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) => silo.AddIncomingGrainCallFilter(async context =>
        {
            if (context.Request.GetMethodName() == nameof(IActivationMigrationManagerSystemTarget.AcceptMigratingGrains))
            {
                carried.TrySetResult(Assert.Single(UnpackMigration(context).Requests!).Id);
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
        var activation = Assert.IsType<ActivationData>(FindActivation(source, grain.GetGrainId()));
        var locator = caller.ServiceProvider.GetRequiredService<Orleans.Runtime.GrainDirectory.GrainLocator>();
        Assert.True(locator.TryLookupInCache(grain.GetGrainId(), out var cached));
        Assert.Equal(source.SiloAddress, cached.SiloAddress);
        var running = grain.Block().AsTask();
        await state.BlockEntered.Task.WaitAsync(Timeout, Cancellation);
        var queued = grain.Record("queued").AsTask();
        await WaitUntilAsync(() => activation.WaitingCount == 1);
        var id = FindPendingCallback(caller, grain.GetGrainId(), nameof(IRetirementHandoffGrain.Record)).Message.Id;
        using var events = new DiagnosticEventCollector(GrainLifecycleEvents.ListenerName);
        var deactivating = events.WaitForEventAsync(nameof(GrainLifecycleEvents.Deactivating),
            e => e.Payload is GrainLifecycleEvents.Deactivating d && ReferenceEquals(d.GrainContext, activation), Timeout, Cancellation);
        try
        {
            if (migrateDirectly)
            {
                activation.Migrate(new() { [IPlacementDirector.PlacementHintKey] = target.SiloAddress }, Cancellation);
            }
            else
            {
                await grain.MigrateOnIdleTo(target.SiloAddress);
            }
            Assert.Equal(ActivationState.Deactivating, activation.State);
            Assert.Equal(DeactivationReasonCode.Migrating, Assert.IsType<GrainLifecycleEvents.Deactivating>((await deactivating).Payload).Reason.ReasonCode);
            Assert.False(carried.Task.IsCompleted);
        }
        finally
        {
            state.ReleaseBlock.TrySetResult();
        }
        await running.WaitAsync(Timeout, Cancellation);
        Assert.Equal(id, await carried.Task.WaitAsync(Timeout, Cancellation));
        await queued.WaitAsync(Timeout, Cancellation);
        Assert.Equal(["queued"], state.Calls);
        Assert.True(locator.TryLookupInCache(grain.GetGrainId(), out cached));
        Assert.Equal(target.SiloAddress, cached.SiloAddress);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateRequestsStayWithTheActivationAndFollowTheOriginalHandoff(bool acceptMigration)
    {
        var arrived = new TaskCompletionSource<CorrelationId>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) => silo.AddIncomingGrainCallFilter(async context =>
        {
            if (context.Request.GetMethodName() == nameof(IActivationMigrationManagerSystemTarget.AcceptMigratingGrains))
            {
                arrived.TrySetResult(Assert.Single(UnpackMigration(context).Requests!).Id);
                await release.Task;
                if (!acceptMigration) throw new InvalidOperationException("The migration fails.");
            }
            await context.Invoke();
        })));
        var source = cluster.Silos[1];
        var target = cluster.Silos[2];
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grain = GetGrain(cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>(), key);
        await PlaceOnAsync(source, () => grain.GetLocation());
        var activation = Assert.IsType<ActivationData>(FindActivation(source, grain.GetGrainId()));
        var running = grain.Block().AsTask();
        await state.BlockEntered.Task.WaitAsync(Timeout, Cancellation);
        var original = grain.Record("migrated").AsTask();
        await WaitUntilAsync(() => activation.WaitingCount == 1);
        var id = FindPendingCallback(cluster.Silos[0], grain.GetGrainId(), nameof(IRetirementHandoffGrain.Record)).Message.Id;
        using var deadline = new CancellationTokenSource();
        Task later;
        try
        {
            activation.Migrate(new() { [IPlacementDirector.PlacementHintKey] = target.SiloAddress }, deadline.Token);
            state.ReleaseBlock.TrySetResult();
            await running.WaitAsync(Timeout, Cancellation);
            Assert.Equal(id, await arrived.Task.WaitAsync(Timeout, Cancellation));
            // Expire the deactivation token while acceptance is in flight, without a real-time sleep.
            // The source must keep the activation and its fixed handoff until the target's outcome is known.
            deadline.Cancel();
            await activation.RunOrQueueTask(() => Task.CompletedTask);
            later = GetGrain(source.ServiceProvider.GetRequiredService<IGrainFactory>(), key).Record("later").AsTask();
            await WaitUntilAsync(() => activation.WaitingCount == 1);
            Assert.Same(activation, FindActivation(source, grain.GetGrainId()));
            Assert.Null(FindActivation(target, grain.GetGrainId()));
            Assert.False(activation.Deactivated.IsCompleted);
        }
        finally
        {
            state.ReleaseBlock.TrySetResult();
            release.TrySetResult();
        }
        if (acceptMigration) await original.WaitAsync(Timeout, Cancellation);
        else await Assert.ThrowsAsync<OrleansMessageRejectionException>(() => original.WaitAsync(Timeout, Cancellation));
        await later.WaitAsync(Timeout, Cancellation);
        await activation.Deactivated.WaitAsync(Timeout, Cancellation);
        Assert.Equal(acceptMigration ? ["migrated", "later"] : new[] { "later" }, state.Calls);
    }

    [Fact]
    public async Task MigrationWhoseAcknowledgementIsLost_RejectsItsRequestsWithoutResendingThem()
    {
        var retargets = 0;
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) => silo.AddIncomingGrainCallFilter(async context =>
        {
            if (context.Request.GetMethodName() == nameof(IActivationMigrationManagerSystemTarget.AcceptMigratingGrains))
            {
                await context.Invoke();
                throw new InvalidOperationException("The acknowledgement is lost.");
            }
            if (context.Request.GetMethodName() == nameof(IActivationMigrationManagerSystemTarget.RetargetMigratedRequests))
                Interlocked.Increment(ref retargets);
            await context.Invoke();
        })));
        var source = cluster.Silos[1];
        var target = cluster.Silos[2];
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grain = GetGrain(cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>(), key);
        await PlaceOnAsync(source, () => grain.GetLocation());
        var activation = Assert.IsType<ActivationData>(FindActivation(source, grain.GetGrainId()));
        var running = grain.Block().AsTask();
        await state.BlockEntered.Task.WaitAsync(Timeout, Cancellation);
        var queued = grain.HoldThenGetLocation().AsTask();
        await WaitUntilAsync(() => activation.WaitingCount == 1);
        try
        {
            await grain.MigrateOnIdleTo(target.SiloAddress);
            state.ReleaseBlock.TrySetResult();
            await running.WaitAsync(Timeout, Cancellation);
            await activation.Deactivated.WaitAsync(Timeout, Cancellation);
            Assert.Equal(0, activation.GetRequestCount());
            Assert.Equal(target.SiloAddress, await state.HoldEntered.Task.WaitAsync(Timeout, Cancellation));
            await Assert.ThrowsAsync<OrleansMessageRejectionException>(() => queued.WaitAsync(Timeout, Cancellation));
        }
        finally
        {
            state.ReleaseBlock.TrySetResult();
            state.ReleaseHold.TrySetResult();
        }
        Assert.Equal(target.SiloAddress, await GetGrain(source.ServiceProvider.GetRequiredService<IGrainFactory>(), key).GetLocation().AsTask().WaitAsync(Timeout, Cancellation));
        Assert.Equal(1, state.HoldCount);
        Assert.Equal(0, Volatile.Read(ref retargets));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedRequestFromCallerOnRetiringSilo_CompletesBeforeCallerDeactivates(bool cancelAfterHandoff)
    {
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) =>
            silo.Configure<SiloMessagingOptions>(options => options.WaitForCancellationAcknowledgement = true)));
        var retiring = cluster.Silos[1];
        var factory = cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>();
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grain = GetGrain(factory, key);
        var sender = factory.GetGrain<IRetirementHandoffCallerGrain>(Guid.NewGuid());
        await PlaceOnAsync(retiring, () => grain.GetLocation());
        await PlaceOnAsync(retiring, () => sender.GetLocation());
        var running = grain.Block().AsTask();
        await state.BlockEntered.Task.WaitAsync(Timeout, Cancellation);
        var call = cancelAfterHandoff ? sender.CallHoldUntilCanceled(key).AsTask() : CompleteCall();
        async Task<bool> CompleteCall()
        {
            Assert.NotEqual(retiring.SiloAddress, await sender.CallHoldThenGetLocation(key));
            return true;
        }
        await WaitUntilAsync(() => GetWaitingCount(retiring, grain.GetGrainId()) == 1);
        var stopping = cluster.StopSiloAsync(retiring, Cancellation);
        try
        {
            await WaitUntilAsync(() => FindActivation(retiring, grain.GetGrainId()) is ActivationData { State: ActivationState.Deactivating });
            state.ReleaseBlock.TrySetResult();
            await running.WaitAsync(Timeout, Cancellation);
            Assert.NotEqual(retiring.SiloAddress, await state.HoldEntered.Task.WaitAsync(Timeout, Cancellation));
            await WaitUntilAsync(() => FindActivation(retiring, grain.GetGrainId()) is null);
            Assert.False(stopping.IsCompleted);
            Assert.False(call.IsCompleted);
            if (cancelAfterHandoff)
            {
                state.Cancellation.Cancel();
                await state.HoldCanceled.Task.WaitAsync(Timeout, Cancellation);
            }
            else state.ReleaseHold.TrySetResult();
            Assert.True(await call.WaitAsync(Timeout, Cancellation));
            await stopping.WaitAsync(Timeout, Cancellation);
        }
        finally
        {
            state.ReleaseBlock.TrySetResult();
            state.ReleaseHold.TrySetResult();
        }
    }

    private static IRetirementHandoffGrain GetGrain(IGrainFactory factory, Guid key)
        => factory.GetGrain<IRetirementHandoffGrain>(key, typeof(RetirementHandoffGrain).FullName);

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
        try { Assert.Equal(silo.SiloAddress, await getLocation().AsTask().WaitAsync(Timeout, Cancellation)); }
        finally { RequestContext.Remove(IPlacementDirector.PlacementHintKey); }
    }

    private static GrainMigrationPackage UnpackMigration(IGrainCallContext context)
        => Assert.Single((List<GrainMigrationPackage>)context.Request.GetArgument(0)!);

    private static GrainAddress CreateSourceAddress(InProcessSiloHandle silo, GrainId grainId)
        => new() { SiloAddress = silo.SiloAddress, GrainId = grainId, ActivationId = ActivationId.NewId() };

    private static Message CreateRequest(InProcessTestCluster cluster, GrainId grainId, long id, object? body = null) => new()
    {
        Direction = Message.Directions.Request,
        Id = new CorrelationId(id),
        SendingGrain = GrainId.Create("test", "caller"),
        SendingSilo = cluster.Silos[0].SiloAddress,
        TargetGrain = grainId,
        BodyObject = body,
    };

    private static Message CreateOneWayRequest(GrainId grainId, long id) => new()
    {
        Direction = Message.Directions.OneWay,
        Id = new CorrelationId(id),
        SendingGrain = GrainId.Create("test", "caller"),
        TargetGrain = grainId,
        TimeToLive = TimeSpan.FromMinutes(1),
    };

    private static CallbackData FindPendingCallback(InProcessSiloHandle silo, GrainId target, string? methodName = null)
    {
        var callbacks = (ConcurrentDictionary<(GrainId, CorrelationId), CallbackData>)typeof(InsideRuntimeClient)
            .GetField("callbacks", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(silo.ServiceProvider.GetRequiredService<InsideRuntimeClient>())!;
        return Assert.Single(callbacks.Values, callback => callback.Message.TargetGrain == target
            && (methodName is null || callback.Message.BodyObject is IInvokable request && request.GetMethodName() == methodName));
    }

    private static IGrainContext? FindActivation(InProcessSiloHandle silo, GrainId grainId)
        => silo.ServiceProvider.GetRequiredService<ActivationDirectory>().FindTarget(grainId);

    private static int GetWaitingCount(InProcessSiloHandle silo, GrainId grainId)
        => FindActivation(silo, grainId) is ActivationData activation ? activation.WaitingCount : 0;

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        timeout.CancelAfter(Timeout);
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}

public interface IRetirementHandoffGrain : IGrainWithGuidKey
{
    ValueTask<SiloAddress> GetLocation();
    ValueTask Block();
    ValueTask<SiloAddress> HoldThenGetLocation();
    ValueTask HoldUntilCanceled(CancellationToken cancellationToken);
    [OneWay] Task RecordOneWay();
    [AlwaysInterleave] ValueTask MigrateOnIdleTo(SiloAddress target);
    ValueTask Record(string call);
    ValueTask<SiloAddress> GetLocationWithPayload(byte[] payload);
    ValueTask<byte[]> EchoPooledPayload(Orleans.Serialization.Buffers.PooledBuffer payload);
}

public interface IRetirementHandoffCallerGrain : IGrainWithGuidKey
{
    ValueTask<SiloAddress> GetLocation();
    ValueTask<SiloAddress> CallHoldThenGetLocation(Guid key);
    ValueTask<bool> CallHoldUntilCanceled(Guid key);
}

[PreferLocalPlacement]
public sealed class RetirementHandoffCallerGrain : Grain, IRetirementHandoffCallerGrain
{
    public ValueTask<SiloAddress> GetLocation() => new(GrainContext.Address.SiloAddress!);
    public ValueTask<SiloAddress> CallHoldThenGetLocation(Guid key)
        => GrainFactory.GetGrain<IRetirementHandoffGrain>(key, typeof(RetirementHandoffGrain).FullName).HoldThenGetLocation();
    public async ValueTask<bool> CallHoldUntilCanceled(Guid key)
    {
        try
        {
            await GrainFactory.GetGrain<IRetirementHandoffGrain>(key, typeof(RetirementHandoffGrain).FullName)
                .HoldUntilCanceled(RetirementHandoffState.Get(key).Cancellation.Token);
            return false;
        }
        catch (OperationCanceledException) { return true; }
    }
}

[PreferLocalPlacement]
public class RetirementHandoffGrain : Grain, IRetirementHandoffGrain
{
    private RetirementHandoffState State => RetirementHandoffState.Get(this.GetPrimaryKey());
    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        if (State.FailActivationOn?.Equals(GrainContext.Address.SiloAddress) == true)
            throw new InvalidOperationException("Destination activation failed.");
        if (State.HoldActivationOn?.Equals(GrainContext.Address.SiloAddress) == true)
        {
            State.ActivationHeld.TrySetResult();
            await State.ReleaseActivation.Task;
        }
    }
    public ValueTask<SiloAddress> GetLocation() => new(GrainContext.Address.SiloAddress!);
    public ValueTask<SiloAddress> GetLocationWithPayload(byte[] payload) => GetLocation();
    public ValueTask<byte[]> EchoPooledPayload(Orleans.Serialization.Buffers.PooledBuffer payload)
    {
        Interlocked.Increment(ref State.PooledPayloadCount);
        State.PooledPayloadActivationId = GrainContext.Address.ActivationId;
        if (RequestContext.Get("pooled") is Orleans.Serialization.Buffers.PooledBuffer context)
        {
            State.PooledRequestContext = context.ToArray();
            context.Dispose();
        }
        var result = payload.ToArray();
        payload.Dispose();
        return new(result);
    }
    public ValueTask Record(string call) { State.Calls.Enqueue(call); return default; }
    public async ValueTask Block() { State.BlockEntered.TrySetResult(); await State.ReleaseBlock.Task; }
    public async ValueTask<SiloAddress> HoldThenGetLocation()
    {
        var address = GrainContext.Address.SiloAddress!;
        Interlocked.Increment(ref State.HoldCount);
        State.HoldEntered.TrySetResult(address);
        await State.ReleaseHold.Task;
        return address;
    }
    public ValueTask MigrateOnIdleTo(SiloAddress target)
    {
        RequestContext.Set(IPlacementDirector.PlacementHintKey, target);
        MigrateOnIdle();
        return default;
    }
    public Task RecordOneWay() { State.OneWayReceived.TrySetResult(GrainContext.Address.SiloAddress!); return Task.CompletedTask; }
    public async ValueTask HoldUntilCanceled(CancellationToken cancellationToken)
    {
        State.HoldEntered.TrySetResult(GrainContext.Address.SiloAddress!);
        try { await State.ReleaseHold.Task.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) { State.HoldCanceled.TrySetResult(); throw; }
    }
}

public sealed class MigrationAdmissionControl : IConfigureGrainContextProvider, IConfigureGrainContext
{
    public Action<IGrainContext>? OnCreate { get; set; }
    public bool TryGetConfigurator(GrainType grainType, GrainProperties properties, out IConfigureGrainContext configurator)
    {
        configurator = this;
        return true;
    }
    public void Configure(IGrainContext context) => OnCreate?.Invoke(context);
}

public sealed class RetirementHandoffState
{
    private static readonly ConcurrentDictionary<Guid, RetirementHandoffState> States = new();
    public TaskCompletionSource BlockEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseBlock { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<SiloAddress> HoldEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseHold { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource HoldCanceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public CancellationTokenSource Cancellation { get; } = new();
    public TaskCompletionSource<SiloAddress> OneWayReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ActivationHeld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseActivation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource DisposeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseDispose { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ConcurrentQueue<string> Calls { get; } = new();
    public SiloAddress? HoldActivationOn { get; set; }
    public SiloAddress? FailActivationOn { get; set; }
    public int HoldCount;
    public int PooledPayloadCount;
    public byte[]? PooledRequestContext;
    public ActivationId? PooledPayloadActivationId;
    public static RetirementHandoffState Create(Guid key) => States[key] = new();
    public static RetirementHandoffState Get(Guid key) => States[key];
}
