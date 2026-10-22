using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using Orleans.Runtime.Scheduler;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Session;
using Orleans.TestingHost;
using Xunit;

namespace UnitTests.ActivationsLifeCycleTests;

public sealed partial class RetirementHandoffTests
{
    [Theory]
    [InlineData("absent")]
    [InlineData("0")]
    [InlineData("2")]
    [InlineData("invalid")]
    [InlineData("65536")]
    public async Task DestinationCapability_UsesVersionOneOrLaterAndOtherwiseForwards(string version)
    {
        var versions = new ConcurrentDictionary<SiloAddress, string>();
        var calls = new ConcurrentQueue<string>();
        bool? carried = null;
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) =>
        {
            silo.Services.AddSingleton<IClusterManifestProvider>(services => new MigrationVersionManifestProvider(
                services.GetRequiredService<Orleans.Runtime.Metadata.ClusterManifestProvider>(),
                services.GetRequiredService<GrainInterfaceTypeResolver>().GetGrainInterfaceType(typeof(IActivationMigrationManagerSystemTarget)), versions));
            silo.AddIncomingGrainCallFilter(async context =>
            {
                if (context.Request.GetMethodName() == nameof(IActivationMigrationManagerSystemTarget.AcceptMigratingGrains))
                {
                    carried = UnpackMigration(context).Requests is not null;
                    calls.Enqueue(context.Request.GetMethodName());
                }
                else if (context.Request.GetMethodName() == nameof(IActivationMigrationManagerSystemTarget.RetargetMigratedRequests))
                    calls.Enqueue(context.Request.GetMethodName());
                await context.Invoke();
            });
        }));
        var source = cluster.Silos[1];
        var target = cluster.Silos[2];
        versions[target.SiloAddress] = version;
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
            Assert.Equal(target.SiloAddress, await state.HoldEntered.Task.WaitAsync(Timeout, Cancellation));
            await activation.Deactivated.WaitAsync(Timeout, Cancellation);
            Assert.Equal(version == "2", carried);
            Assert.Equal(version == "2"
                ? [nameof(IActivationMigrationManagerSystemTarget.AcceptMigratingGrains), nameof(IActivationMigrationManagerSystemTarget.RetargetMigratedRequests)]
                : new[] { nameof(IActivationMigrationManagerSystemTarget.AcceptMigratingGrains) }, calls);
            state.ReleaseHold.TrySetResult();
            Assert.Equal(target.SiloAddress, await queued.WaitAsync(Timeout, Cancellation));
            Assert.Equal(1, state.HoldCount);
        }
        finally { state.ReleaseBlock.TrySetResult(); state.ReleaseHold.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedRequestsTooLargeToMigrateTogether_AreForwardedWithConsumedPooledArgumentsRestored(bool localCaller)
    {
        const int Limit = 1024 * 1024;
        var carried = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) =>
        {
            silo.Configure<SiloMessagingOptions>(options => options.MaxMessageBodySize = Limit);
            silo.AddIncomingGrainCallFilter(async context =>
            {
                if (context.Request.GetMethodName() == nameof(IActivationMigrationManagerSystemTarget.AcceptMigratingGrains))
                    carried.TrySetResult(UnpackMigration(context).Requests is not null);
                await context.Invoke();
            });
        }));
        var source = cluster.Silos[1];
        var target = cluster.Silos[2];
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grain = GetGrain((localCaller ? source : cluster.Silos[0]).ServiceProvider.GetRequiredService<IGrainFactory>(), key);
        await PlaceOnAsync(source, () => grain.GetLocation());
        var running = grain.Block().AsTask();
        await state.BlockEntered.Task.WaitAsync(Timeout, Cancellation);
        var payload = Enumerable.Repeat((byte)0x6D, Limit * 3 / 5).ToArray();
        var first = grain.EchoPooledPayload(CreatePooledBuffer(payload)).AsTask();
        var secondPayload = Enumerable.Repeat((byte)0x92, Limit * 3 / 5).ToArray();
        var second = grain.EchoPooledPayload(CreatePooledBuffer(secondPayload)).AsTask();
        await WaitUntilAsync(() => GetWaitingCount(source, grain.GetGrainId()) == 2);
        try
        {
            await grain.MigrateOnIdleTo(target.SiloAddress);
            state.ReleaseBlock.TrySetResult();
            await running.WaitAsync(Timeout, Cancellation);
            Assert.False(await carried.Task.WaitAsync(Timeout, Cancellation));
            Assert.Equal(payload, await first.WaitAsync(Timeout, Cancellation));
            Assert.Equal(secondPayload, await second.WaitAsync(Timeout, Cancellation));
            Assert.Equal(target.SiloAddress, await grain.GetLocation().AsTask().WaitAsync(Timeout, Cancellation));
        }
        finally { state.ReleaseBlock.TrySetResult(); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Migration_RetainsRequestsWhichDoNotFitWithTheMigrationContext(bool sizeInRequestContext, bool contextExceedsBudget)
    {
        const int Limit = 1024 * 1024;
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) =>
            silo.Configure<SiloMessagingOptions>(options => options.MaxMessageBodySize = Limit)));
        var source = cluster.Silos[1];
        var target = cluster.Silos[2];
        var manager = source.ServiceProvider.GetRequiredService<ActivationMigrationManager>();
        var key = Guid.NewGuid();
        RetirementHandoffState.Create(key);
        var grainId = GetGrain(cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>(), key).GetGrainId();
        var context = new MigrationContext(source.ServiceProvider.GetRequiredService<SerializerSessionPool>());
        Assert.True(context.TryAddValue(new string('k', contextExceedsBudget ? Limit : Limit / 2), 42));
        var request = CreateOneWayRequest(grainId, 1);
        request.BodyObject = sizeInRequestContext ? new byte[16] : new byte[Limit * 3 / 5];
        if (sizeInRequestContext) request.RequestContextData = new() { ["large"] = new byte[Limit * 3 / 5] };
        if (contextExceedsBudget)
        {
            // The context alone rules out handoff. Do not even try to serialize the retained request.
            request.BodyObject = new UnserializableValue();
            using var cancellation = new CancellationTokenSource();
            ActivationMigration? retained = null;
            await manager.RunOrQueueTask(() =>
            {
                retained = manager.StartMigration(target.SiloAddress, CreateSourceAddress(source, grainId), context, [request], cancellation.Token);
                Assert.Null(retained.Package.Requests);
                Assert.Null(retained.Frames);
                cancellation.Cancel();
                return Task.CompletedTask;
            });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retained!.Completion.WaitAsync(Timeout, Cancellation));
            return;
        }
        var migration = manager.StartMigration(target.SiloAddress, CreateSourceAddress(source, grainId), context, [request], Cancellation);
        Assert.Null(migration.Package.Requests);
        Assert.NotEmpty(migration.Frames!);
        await migration.Completion.WaitAsync(Timeout, Cancellation);
    }

    [Fact]
    public async Task CarryingRequests_LeavesTheMigrationContextIntact()
    {
        await using var cluster = await DeployAsync();
        var source = cluster.Silos[1];
        var target = cluster.Silos[2];
        var manager = source.ServiceProvider.GetRequiredService<ActivationMigrationManager>();
        var key = Guid.NewGuid();
        RetirementHandoffState.Create(key);
        var grain = GetGrain(cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>(), key);
        await PlaceOnAsync(target, () => grain.GetLocation());
        var marker = new string('x', 10_000);
        var context = new MigrationContext(source.ServiceProvider.GetRequiredService<SerializerSessionPool>());
        Assert.True(context.TryAddValue("marker", marker));
        ActivationMigration? migration = null;
        await manager.RunOrQueueTask(() =>
        {
            migration = manager.StartMigration(target.SiloAddress, CreateSourceAddress(source, grain.GetGrainId()), context,
                [CreateOneWayRequest(grain.GetGrainId(), 1)], Cancellation);
            Assert.NotNull(migration.Package.Requests);
            Assert.True(context.TryGetValue("marker", out string? value));
            Assert.Equal(marker, value);
            return Task.CompletedTask;
        });
        await migration!.Completion.WaitAsync(Timeout, Cancellation);
    }

    [Fact]
    public async Task MigrationCanceledBeforeItIsSent_IsNotSentAndForwardsItsRequests()
    {
        var received = 0;
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) => silo.AddIncomingGrainCallFilter(async context =>
        {
            if (context.Request.GetMethodName() == nameof(IActivationMigrationManagerSystemTarget.AcceptMigratingGrains)) Interlocked.Increment(ref received);
            await context.Invoke();
        })));
        var source = cluster.Silos[1];
        var target = cluster.Silos[2];
        var key = Guid.NewGuid();
        RetirementHandoffState.Create(key);
        var grainId = GetGrain(cluster.Silos[0].ServiceProvider.GetRequiredService<IGrainFactory>(), key).GetGrainId();
        var original = CreateRequest(cluster, grainId, 1, CreatePooledBuffer(Enumerable.Repeat((byte)0x5A, 20_000).ToArray()));
        List<Message> requests = [original];
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var context = new MigrationContext(source.ServiceProvider.GetRequiredService<SerializerSessionPool>());
        Assert.True(context.TryAddValue("state", 42));
        var migration = source.ServiceProvider.GetRequiredService<ActivationMigrationManager>().StartMigration(target.SiloAddress,
            CreateSourceAddress(source, grainId), context, requests, canceled.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => migration.Completion.WaitAsync(Timeout, Cancellation));
        Assert.False(migration.TrySend());
        Assert.Equal(0, Volatile.Read(ref received));
        Assert.Empty(context.Keys);
        migration.Deliver();
        Assert.Same(original, Assert.Single(requests));
        Assert.Same(requests, migration.Package.Requests);
    }

    private static PooledBuffer CreatePooledBuffer(byte[] bytes)
    {
        var result = new PooledBuffer();
        result.Write(bytes);
        return result;
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Migration_SizingAcceptsExactlyTheBudgetAndRetainsTheFirstByteOver(int delta)
    {
        const int Limit = 1024 * 1024;
        const int RpcAllowance = 1024;
        const int PackageAllowance = 64;
        const int RequestAllowance = 16;
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) =>
            silo.Configure<SiloMessagingOptions>(options => options.MaxMessageBodySize = Limit)));
        var source = cluster.Silos[1];
        var target = cluster.Silos[2];
        var manager = source.ServiceProvider.GetRequiredService<ActivationMigrationManager>();
        var grainId = GrainId.Create("test", "sizing-boundary");
        var request = CreateOneWayRequest(grainId, 1);
        request.BodyObject = new byte[Limit / 2];
        var context = new MigrationContext(source.ServiceProvider.GetRequiredService<SerializerSessionPool>());
        var packageSize = PackageAllowance + context.GetMaxSerializedSize() + grainId.Type.AsSpan().Length + grainId.Key.AsSpan().Length;
        using var serializer = source.ServiceProvider.GetRequiredService<MessageSerializer>();
        var overhead = MessageCodec.PrepareFrame(serializer, request).Length - Limit / 2;
        var bodyLength = checked((int)(Limit - RpcAllowance - packageSize - RequestAllowance - overhead + delta));
        request.BodyObject = new byte[bodyLength];
        var actualSize = packageSize + RequestAllowance + MessageCodec.PrepareFrame(serializer, request).Length;
        Assert.Equal(Limit - RpcAllowance + delta, actualSize);
        using var cancellation = new CancellationTokenSource();
        ActivationMigration? migration = null;
        await manager.RunOrQueueTask(() =>
        {
            migration = manager.StartMigration(target.SiloAddress, CreateSourceAddress(source, grainId), context, [request], cancellation.Token);
            if (delta <= 0)
            {
                Assert.Same(request, Assert.Single(migration.Package.Requests!));
                Assert.Equal(actualSize, migration.Size);
            }
            else Assert.Null(migration.Package.Requests);
            cancellation.Cancel();
            return Task.CompletedTask;
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => migration!.Completion.WaitAsync(Timeout, Cancellation));
    }

    [Fact]
    public async Task MigrationQueuedBehindABlockedBatch_CancellationRestoresPooledBodyAndRequestContextBeforeForwarding()
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new ConcurrentQueue<GrainId>();
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) => silo.AddIncomingGrainCallFilter(async context =>
        {
            if (context.Request.GetMethodName() == nameof(IActivationMigrationManagerSystemTarget.AcceptMigratingGrains))
            {
                foreach (var package in (List<GrainMigrationPackage>)context.Request.GetArgument(0)!) received.Enqueue(package.GrainId);
                arrived.TrySetResult();
                await release.Task;
            }
            await context.Invoke();
        })));
        var source = cluster.Silos[1];
        var target = cluster.Silos[2];
        var manager = source.ServiceProvider.GetRequiredService<ActivationMigrationManager>();
        var factory = source.ServiceProvider.GetRequiredService<IGrainFactory>();
        var pool = source.ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var blockedKey = Guid.NewGuid();
        RetirementHandoffState.Create(blockedKey);
        var blockedId = GetGrain(factory, blockedKey).GetGrainId();
        var blocked = manager.StartMigration(target.SiloAddress, CreateSourceAddress(source, blockedId), new MigrationContext(pool),
            [CreateOneWayRequest(blockedId, 1)], CancellationToken.None);
        await arrived.Task.WaitAsync(Timeout, Cancellation);
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grain = GetGrain(factory, key);
        await PlaceOnAsync(source, () => grain.GetLocation());
        var activation = Assert.IsType<ActivationData>(FindActivation(source, grain.GetGrainId()));
        var running = grain.Block().AsTask();
        await state.BlockEntered.Task.WaitAsync(Timeout, Cancellation);
        var bytes = Enumerable.Repeat((byte)0x5B, 20_000).ToArray();
        var contextBytes = Enumerable.Repeat((byte)0x71, 30_000).ToArray();
        Task<byte[]> call;
        RequestContext.Set("pooled", CreatePooledBuffer(contextBytes));
        RequestContext.Set(Orleans.Runtime.Placement.IPlacementDirector.PlacementHintKey, target.SiloAddress);
        try { call = grain.EchoPooledPayload(CreatePooledBuffer(bytes)).AsTask(); }
        finally
        {
            RequestContext.Remove("pooled");
            RequestContext.Remove(Orleans.Runtime.Placement.IPlacementDirector.PlacementHintKey);
        }
        await WaitUntilAsync(() => activation.WaitingCount == 1);
        using var deadline = new CancellationTokenSource();
        try
        {
            activation.Migrate(new() { [Orleans.Runtime.Placement.IPlacementDirector.PlacementHintKey] = target.SiloAddress }, deadline.Token);
            state.ReleaseBlock.TrySetResult();
            await running.WaitAsync(Timeout, Cancellation);
            // The fixed handoff has left the activation's queue. A scheduler barrier follows synchronous frame preparation.
            await WaitUntilAsync(() => activation.WaitingCount == 0);
            await activation.RunOrQueueTask(() => Task.CompletedTask);
            Assert.False(activation.Deactivated.IsCompleted);
            Assert.False(blocked.Completion.IsCompleted);
            deadline.Cancel();
            await activation.Deactivated.WaitAsync(Timeout, Cancellation);
            Assert.Equal(bytes, await call.WaitAsync(Timeout, Cancellation));
            Assert.Equal(contextBytes, state.PooledRequestContext);
            Assert.Equal(1, state.PooledPayloadCount);
            // Acceptance never happened, so ordinary directory repair determines the replacement's location.
            // Prove forwarding to a NEW activation rather than imposing the canceled migration's placement.
            Assert.Equal(ActivationState.Invalid, activation.State);
            Assert.Equal(0, activation.GetRequestCount());
            Assert.NotEqual(activation.ActivationId, state.PooledPayloadActivationId);
        }
        finally
        {
            state.ReleaseBlock.TrySetResult();
            release.TrySetResult();
        }
        await blocked.Completion.WaitAsync(Timeout, Cancellation);
        Assert.Equal([blockedId], received);
    }

    [Fact]
    public async Task CorruptPreparedFrame_RejectsEntireForwardingGroupWithoutReplay()
    {
        const int Limit = 1024 * 1024;
        var corrupted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cluster = await DeployAsync(builder => builder.ConfigureSilo((_, silo) =>
        {
            silo.Configure<SiloMessagingOptions>(options => options.MaxMessageBodySize = Limit);
            silo.AddOutgoingGrainCallFilter(async context =>
            {
                if (context.Request.GetMethodName() == nameof(IActivationMigrationManagerSystemTarget.AcceptMigratingGrains))
                {
                    var package = UnpackMigration(context);
                    Assert.Null(package.Requests);
                    Assert.NotEmpty(package.Source!.Frames!);
                    package.Source.Frames![0].AsSpan(0, sizeof(int)).Fill(0xFF);
                    corrupted.TrySetResult();
                }
                await context.Invoke();
            });
        }));
        var source = cluster.Silos[1];
        var target = cluster.Silos[2];
        var key = Guid.NewGuid();
        var state = RetirementHandoffState.Create(key);
        var grain = GetGrain(source.ServiceProvider.GetRequiredService<IGrainFactory>(), key);
        await PlaceOnAsync(source, () => grain.GetLocation());
        var running = grain.Block().AsTask();
        await state.BlockEntered.Task.WaitAsync(Timeout, Cancellation);
        var first = grain.EchoPooledPayload(CreatePooledBuffer(new byte[Limit * 3 / 5])).AsTask();
        var second = grain.EchoPooledPayload(CreatePooledBuffer(new byte[Limit * 3 / 5])).AsTask();
        await WaitUntilAsync(() => GetWaitingCount(source, grain.GetGrainId()) == 2);
        try
        {
            await grain.MigrateOnIdleTo(target.SiloAddress);
            state.ReleaseBlock.TrySetResult();
            await running.WaitAsync(Timeout, Cancellation);
            await corrupted.Task.WaitAsync(Timeout, Cancellation);
            var firstError = await Assert.ThrowsAsync<InvalidMessageFrameException>(() => first.WaitAsync(Timeout, Cancellation));
            var secondError = await Assert.ThrowsAsync<InvalidMessageFrameException>(() => second.WaitAsync(Timeout, Cancellation));
            Assert.StartsWith("Invalid header size: -1", firstError.Message);
            Assert.Equal(firstError.Message, secondError.Message);
            Assert.Equal(target.SiloAddress, await grain.GetLocation().AsTask().WaitAsync(Timeout, Cancellation));
            Assert.Equal(0, state.PooledPayloadCount);
        }
        finally { state.ReleaseBlock.TrySetResult(); }
    }
}

internal sealed class MigrationVersionManifestProvider(
    IClusterManifestProvider inner,
    GrainInterfaceType migrationInterface,
    ConcurrentDictionary<SiloAddress, string> versions) : IClusterManifestProvider
{
    public ClusterManifest Current
    {
        get
        {
            var manifest = inner.Current;
            var silos = manifest.Silos;
            foreach (var (silo, version) in versions)
            {
                if (silos.TryGetValue(silo, out var siloManifest) && siloManifest.Interfaces.TryGetValue(migrationInterface, out var properties))
                {
                    var values = version == "absent"
                        ? properties.Properties.Remove(WellKnownGrainInterfaceProperties.Version)
                        : properties.Properties.SetItem(WellKnownGrainInterfaceProperties.Version, version);
                    silos = silos.SetItem(silo, new GrainManifest(siloManifest.Grains,
                        siloManifest.Interfaces.SetItem(migrationInterface, new GrainInterfaceProperties(values))));
                }
            }
            return new ClusterManifest(manifest.Version, silos);
        }
    }
    public IAsyncEnumerable<ClusterManifest> Updates => inner.Updates;
    public GrainManifest LocalGrainManifest => inner.LocalGrainManifest;
}
