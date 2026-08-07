using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Metadata;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Streams;
using TestExtensions;
using Xunit;

namespace UnitTests.StreamingTests;

[TestCategory("BVT")]
public sealed class GrainStreamQueueCheckpointerTests : StreamQueueCheckpointerTests
{
    protected override OffsetRegressionPolicy RegressionPolicy
        => OffsetRegressionPolicy.PersistLatestUpdate;

    protected override Task<IStreamQueueCheckpointer<string>> CreateCheckpointer(
        ControllableCheckpointStore store)
    {
        var grain = Substitute.For<IStreamCheckpointerGrain>();
        grain.Load(Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<string>(store.Load()));
        grain.Update(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => new ValueTask<string>(store.Write(call.ArgAt<string>(0))));
        return Task.FromResult<IStreamQueueCheckpointer<string>>(
            new GrainStreamQueueCheckpointer(grain));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_WhenPersistIntervalIsNotPositive_Throws(long ticks)
    {
        var grain = Substitute.For<IStreamCheckpointerGrain>();
        var options = new GrainStreamQueueCheckpointerOptions
        {
            PersistInterval = TimeSpan.FromTicks(ticks),
        };

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new GrainStreamQueueCheckpointer(grain, options));

        Assert.Equal("options", exception.ParamName);
        Assert.Equal(options.PersistInterval, exception.ActualValue);
    }

    [Fact]
    public async Task Update_WhenOffsetIsNull_ThrowsWithoutWriting()
    {
        var store = new ControllableCheckpointStore("10");
        var checkpointer = await CreateCheckpointer(store);
        Assert.Equal("10", await checkpointer.Load());

        Assert.Throws<ArgumentNullException>(
            () => checkpointer.Update(null!, DateTime.UtcNow));

        Assert.Empty(store.WriteAttempts);
        Assert.Equal("10", store.PersistedCheckpoint);
    }

    [Fact]
    public void StateHasOrleansSerializerMetadata()
    {
        Assert.NotNull(typeof(StreamCheckpointerGrainState).GetCustomAttributes(typeof(GenerateSerializerAttribute), inherit: false).SingleOrDefault());
        var checkpoint = typeof(StreamCheckpointerGrainState).GetProperty(nameof(StreamCheckpointerGrainState.Checkpoint));
        Assert.NotNull(checkpoint?.GetCustomAttributes(typeof(IdAttribute), inherit: false).SingleOrDefault());
    }

    [Fact]
    public void ConfiguredGrainInterface_UsesConfiguredGrainType()
    {
        var attribute = Assert.IsType<DefaultGrainTypeAttribute>(
            Assert.Single(typeof(IConfiguredStreamCheckpointerGrain).GetCustomAttributes(
                typeof(DefaultGrainTypeAttribute),
                inherit: false)));
        var properties = new Dictionary<string, string>();

        ((IGrainInterfacePropertiesProviderAttribute)attribute).Populate(
            null!,
            typeof(IConfiguredStreamCheckpointerGrain),
            properties);

        Assert.Equal(
            "stream.checkpoint.configured",
            properties[WellKnownGrainInterfaceProperties.DefaultGrainType]);
    }

    [Fact]
    public async Task FlushAsync_ForwardsCancellationTokenToGrainWrite()
    {
        var grain = Substitute.For<IStreamCheckpointerGrain>();
        grain.Load(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult("10"));
        grain.Update(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => ValueTask.FromResult(call.ArgAt<string>(0)));
        var checkpointer = new GrainStreamQueueCheckpointer(grain);
        await checkpointer.Load();
        var utcNow = DateTime.UtcNow;
        checkpointer.Update("20", utcNow);
        checkpointer.Update("30", utcNow);
        using var cancellation = new CancellationTokenSource();

        await checkpointer.FlushAsync(cancellation.Token);

        await grain.Received(1).Update("20", "10", CancellationToken.None);
        await grain.Received(1).Update("30", "20", cancellation.Token);
    }

    [Fact]
    public async Task GrainMethods_WhenCanceled_DoNotReadOrWriteState()
    {
        var state = new StreamCheckpointerGrainState { Checkpoint = "10" };
        var storage = Substitute.For<IPersistentState<StreamCheckpointerGrainState>>();
        storage.State.Returns(state);
        var grain = CreateGrain(storage);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => grain.Load(cancellation.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => grain.Update("20", "10", cancellation.Token).AsTask());

        Assert.Equal("10", state.Checkpoint);
        await storage.DidNotReceive().WriteStateAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GrainUpdate_ForwardsCancellationTokenToStorage()
    {
        var state = new StreamCheckpointerGrainState { Checkpoint = "10" };
        var storage = Substitute.For<IPersistentState<StreamCheckpointerGrainState>>();
        storage.State.Returns(state);
        var grain = CreateGrain(storage);
        using var cancellation = new CancellationTokenSource();

        var checkpoint = await grain.Update("20", "10", cancellation.Token);

        Assert.Equal("20", checkpoint);
        Assert.Equal("20", state.Checkpoint);
        await storage.Received(1).WriteStateAsync(cancellation.Token);
    }

    [Fact]
    public async Task GrainUpdate_WhenExpectedCheckpointIsStale_DoesNotWrite()
    {
        var state = new StreamCheckpointerGrainState { Checkpoint = "20" };
        var storage = Substitute.For<IPersistentState<StreamCheckpointerGrainState>>();
        storage.State.Returns(state);
        var grain = CreateGrain(storage);

        var checkpoint = await grain.Update("30", "10", CancellationToken.None);

        Assert.Equal("20", checkpoint);
        Assert.Equal("20", state.Checkpoint);
        await storage.DidNotReceive().WriteStateAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GrainUpdate_WhenStorageWriteFails_RestoresPreviousCheckpoint()
    {
        var expected = new InvalidOperationException("checkpoint write failed");
        var state = new StreamCheckpointerGrainState { Checkpoint = "10" };
        var storage = Substitute.For<IPersistentState<StreamCheckpointerGrainState>>();
        storage.State.Returns(state);
        storage.WriteStateAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException(expected));
        var grain = CreateGrain(storage);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => grain.Update("20", "10", CancellationToken.None).AsTask());

        Assert.Same(expected, actual);
        Assert.Equal("10", state.Checkpoint);
    }

    [Fact]
    public async Task OrderedCheckpointers_WhenOneIsStale_DoNotRegressPersistedCheckpoint()
    {
        var state = new StreamCheckpointerGrainState { Checkpoint = "20" };
        var storage = Substitute.For<IPersistentState<StreamCheckpointerGrainState>>();
        storage.State.Returns(state);
        var grain = CreateGrain(storage);
        var options = new GrainStreamQueueCheckpointerOptions
        {
            CheckpointComparer = StreamCheckpointComparers.Numeric,
        };
        var first = new GrainStreamQueueCheckpointer(grain, options);
        var second = new GrainStreamQueueCheckpointer(grain, options);
        await first.Load();
        await second.Load();

        first.Update("40", DateTime.UtcNow);
        await first.FlushAsync(CancellationToken.None);
        second.Update("30", DateTime.UtcNow);
        await second.FlushAsync(CancellationToken.None);

        Assert.Equal("40", state.Checkpoint);
        await storage.Received(1).WriteStateAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FlushAsync_WhenRetryFails_PropagatesFailure()
    {
        var expected = new InvalidOperationException("checkpoint write failed");
        var grain = Substitute.For<IStreamCheckpointerGrain>();
        grain.Load(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult("10"));
        grain.Update(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromException<string>(expected));
        var checkpointer = new GrainStreamQueueCheckpointer(grain);
        await checkpointer.Load();
        checkpointer.Update("20", DateTime.UtcNow);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => checkpointer.FlushAsync(CancellationToken.None));

        Assert.Same(expected, actual);
        await grain.Received(2).Update("20", "10", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Factory_UsesProviderSpecificServiceId()
    {
        const string providerName = "provider";
        var grain = Substitute.For<IStreamCheckpointerGrain>();
        grain.Load(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(string.Empty));
        var clusterClient = Substitute.For<IClusterClient>();
        clusterClient.GetGrain<IStreamCheckpointerGrain>(Arg.Any<string>()).Returns(grain);
        using var services = new ServiceCollection()
            .AddOptions()
            .Configure<ClusterOptions>(options => options.ServiceId = "global-service")
            .AddOptions<GrainStreamQueueCheckpointerOptions>(providerName)
            .Services
            .AddSingleton(clusterClient)
            .AddKeyedSingleton(
                providerName,
                new ClusterOptions { ServiceId = "provider-service" })
            .BuildServiceProvider();

        var factory = GrainStreamQueueCheckpointerFactory.CreateFactory(services, providerName);
        _ = await factory.Create("partition");

        _ = clusterClient.Received(1).GetGrain<IStreamCheckpointerGrain>(
            GrainStreamQueueCheckpointer.GetGrainKey(
                providerName,
                "provider-service",
                "partition",
                ProviderConstants.DEFAULT_PUBSUB_PROVIDER_NAME));
    }

    [Theory]
    [InlineData("provider_one", "service", "partition", "provider", "one_service", "partition")]
    [InlineData("provider", "one_service", "partition", "provider", "one", "service_partition")]
    public void GrainKey_IncludesServiceProviderAndPartitionWithoutAmbiguity(
        string firstProvider,
        string firstService,
        string firstPartition,
        string secondProvider,
        string secondService,
        string secondPartition)
    {
        var first = GrainStreamQueueCheckpointer.GetGrainKey(
            firstProvider,
            firstService,
            firstPartition,
            ProviderConstants.DEFAULT_PUBSUB_PROVIDER_NAME);
        var second = GrainStreamQueueCheckpointer.GetGrainKey(
            secondProvider,
            secondService,
            secondPartition,
            ProviderConstants.DEFAULT_PUBSUB_PROVIDER_NAME);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void GrainKey_UsesStorageCompatibleSeparator()
    {
        var key = GrainStreamQueueCheckpointer.GetGrainKey(
            "event-hubs",
            "my-service",
            "0",
            ProviderConstants.DEFAULT_PUBSUB_PROVIDER_NAME);

        Assert.Equal("10-my-service10-event-hubs1-0", key);
    }

    [Fact]
    public async Task Create_DefaultStorageWithReservedPrefix_UsesDefaultGrainType()
    {
        const string providerName = "__orleans_storage_provider__:QQ==:provider";
        var grain = Substitute.For<IStreamCheckpointerGrain>();
        grain.Load(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(string.Empty));
        var clusterClient = Substitute.For<IClusterClient>();
        clusterClient
            .GetGrain<IStreamCheckpointerGrain>(Arg.Any<string>())
            .Returns(grain);

        _ = await GrainStreamQueueCheckpointer.Create(
            providerName,
            "partition",
            "service",
            clusterClient,
            new GrainStreamQueueCheckpointerOptions());

        _ = clusterClient.Received(1).GetGrain<IStreamCheckpointerGrain>(
            GrainStreamQueueCheckpointer.GetGrainKey(
                providerName,
                "service",
                "partition",
                ProviderConstants.DEFAULT_PUBSUB_PROVIDER_NAME));
    }

    [Fact]
    public void CustomStorageProvider_IsSelectedFromGrainKey()
    {
        const string storageProviderName = "CheckpointStore";
        var storage = Substitute.For<IPersistentState<StreamCheckpointerGrainState>>();
        var context = Substitute.For<IGrainContext>();
        context.GrainId.Returns(GrainId.Create(
            "stream.checkpoint.configured",
            GrainStreamQueueCheckpointer.GetGrainKey(
                "provider",
                "service",
                "partition",
                storageProviderName)));
        var persistentStateFactory = Substitute.For<IPersistentStateFactory>();
        IPersistentStateConfiguration? configuration = null;
        persistentStateFactory
            .Create<StreamCheckpointerGrainState>(
                context,
                Arg.Any<IPersistentStateConfiguration>())
            .Returns(call =>
            {
                configuration = call.ArgAt<IPersistentStateConfiguration>(1);
                return storage;
            });

        _ = new ConfiguredStreamCheckpointGrain(context, persistentStateFactory);

        Assert.NotNull(configuration);
        Assert.Equal("chk", configuration.StateName);
        Assert.Equal(storageProviderName, configuration.StorageName);
    }

    private static StreamCheckpointGrain CreateGrain(
        IPersistentState<StreamCheckpointerGrainState> storage)
        => new(storage);

}

[TestCategory("BVT")]
public sealed class OrderedGrainStreamQueueCheckpointerTests : StreamQueueCheckpointerTests
{
    private static readonly DateTime TestTimeUtc = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    protected override OffsetRegressionPolicy RegressionPolicy
        => OffsetRegressionPolicy.Ignore;

    protected override TimeSpan PersistInterval => TimeSpan.FromSeconds(5);

    protected override string EquivalentCheckpoint => "020";

    protected override Task<IStreamQueueCheckpointer<string>> CreateCheckpointer(
        ControllableCheckpointStore store)
    {
        var grain = Substitute.For<IStreamCheckpointerGrain>();
        grain.Load(Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<string>(store.Load()));
        grain.Update(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => new ValueTask<string>(store.Write(call.ArgAt<string>(0))));
        return Task.FromResult<IStreamQueueCheckpointer<string>>(
            new GrainStreamQueueCheckpointer(
                grain,
                new GrainStreamQueueCheckpointerOptions
                {
                    CheckpointComparer = StreamCheckpointComparers.Numeric,
                    PersistInterval = PersistInterval,
                }));
    }

    [Fact]
    public async Task Update_WithArbitrarySizeNumericOffsets_PersistsOnlyAdvance()
    {
        const string initial = "99999999999999999999999999999999999999999999999999";
        const string regressed = "88888888888888888888888888888888888888888888888888";
        const string advanced = "100000000000000000000000000000000000000000000000000";
        var store = new ControllableCheckpointStore(initial);
        var checkpointer = await CreateCheckpointer(store);
        Assert.Equal(initial, await checkpointer.Load());

        checkpointer.Update(regressed, TestTimeUtc);
        checkpointer.Update(advanced, TestTimeUtc);
        await store.WaitForCompletedWrites(1);
        await checkpointer.FlushAsync(CancellationToken.None);

        Assert.Equal([advanced], store.WriteAttempts);
        Assert.Equal([advanced], store.CompletedWrites);
        Assert.Equal(advanced, store.PersistedCheckpoint);
    }

    [Theory]
    [InlineData("20", "not-an-offset")]
    [InlineData("not-an-offset", "20")]
    public async Task Update_WhenEitherNumericOffsetIsMalformed_DoesNotAdvance(
        string initial,
        string candidate)
    {
        var store = new ControllableCheckpointStore(initial);
        var checkpointer = await CreateCheckpointer(store);
        Assert.Equal(initial, await checkpointer.Load());

        checkpointer.Update(candidate, TestTimeUtc);
        await checkpointer.FlushAsync(CancellationToken.None);

        Assert.Empty(store.WriteAttempts);
        Assert.Empty(store.CompletedWrites);
        Assert.Equal(initial, store.PersistedCheckpoint);
    }

}
