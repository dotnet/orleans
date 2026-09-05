using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Persistence.EntityFrameworkCore.Data;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Activators;
using Orleans.Serialization.Serializers;
using Orleans.Storage;
using TestExtensions;

namespace Orleans.EntityFrameworkCore.Tests.Persistence;

[TestArea("EFCore")]
[TestProvider("None")]
[TestSuite("BVT")]
public sealed class EFGrainStorageActivationTests
{
    [Fact]
    public async Task PR8654_Persistence_ReadStateAsync_MissingStateUsesOrleansActivator()
    {
        await using var fixture = await ActivationFixture.Create();
        var state = new GrainState<ConstructorRestrictedSerializableState>();

        await fixture.Storage.ReadStateAsync(
            "constructor-restricted",
            GrainId.Create("activation", $"missing-{Guid.NewGuid():N}"),
            state);

        Assert.False(state.RecordExists);
        Assert.Null(state.ETag);
        Assert.Same(fixture.ActivatedState, state.State);
        Assert.Equal("created-by-orleans-activator", state.State?.Marker);
        Assert.Equal(1, fixture.ActivatorProvider.GetActivatorCallCount);
        Assert.Equal(1, fixture.Activator.CreateCallCount);
        await using var context = fixture.Factory.CreateDbContext();
        Assert.Empty(await context.GrainState.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task PR8654_Persistence_ClearStateAsync_ResetUsesOrleansActivator()
    {
        await using var fixture = await ActivationFixture.Create();
        var grainId = GrainId.Create("activation", $"clear-{Guid.NewGuid():N}");
        var persistedState = ConstructorRestrictedSerializableState.Create("persisted-before-clear");
        var state = new GrainState<ConstructorRestrictedSerializableState>(persistedState);
        await fixture.Storage.WriteStateAsync("constructor-restricted", grainId, state);
        Assert.True(state.RecordExists);
        Assert.NotNull(state.ETag);

        await fixture.Storage.ClearStateAsync("constructor-restricted", grainId, state);

        Assert.False(state.RecordExists);
        Assert.Null(state.ETag);
        Assert.NotSame(persistedState, state.State);
        Assert.Same(fixture.ActivatedState, state.State);
        Assert.Equal("created-by-orleans-activator", state.State?.Marker);
        Assert.Equal(1, fixture.ActivatorProvider.GetActivatorCallCount);
        Assert.Equal(1, fixture.Activator.CreateCallCount);
        await using var context = fixture.Factory.CreateDbContext();
        Assert.Empty(await context.GrainState.AsNoTracking().ToListAsync());
    }

    [GenerateSerializer]
    public sealed class ConstructorRestrictedSerializableState
    {
        private ConstructorRestrictedSerializableState(string marker)
        {
            Marker = marker;
        }

        [Id(0)]
        public string Marker { get; private set; }

        public static ConstructorRestrictedSerializableState Create(string marker) => new(marker);
    }

    private sealed class ActivationFixture : IAsyncDisposable
    {
        private const string StorageName = "ActivationRegression";
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _services;

        private ActivationFixture(
            SqliteConnection connection,
            TestDbContextFactory factory,
            ServiceProvider services,
            EFGrainStorage<TestGrainStateDbContext, Guid> storage,
            ConstructorRestrictedSerializableState activatedState,
            RecordingActivatorProvider activatorProvider,
            RecordingActivator activator)
        {
            _connection = connection;
            _services = services;
            Factory = factory;
            Storage = storage;
            ActivatedState = activatedState;
            ActivatorProvider = activatorProvider;
            Activator = activator;
        }

        public TestDbContextFactory Factory { get; }

        public EFGrainStorage<TestGrainStateDbContext, Guid> Storage { get; }

        public ConstructorRestrictedSerializableState ActivatedState { get; }

        public RecordingActivatorProvider ActivatorProvider { get; }

        public RecordingActivator Activator { get; }

        public static async Task<ActivationFixture> Create()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<TestGrainStateDbContext>()
                .UseSqlite(connection)
                .Options;
            var factory = new TestDbContextFactory(options);
            await using (var context = factory.CreateDbContext())
            {
                await context.Database.EnsureCreatedAsync();
            }

            var activatedState =
                ConstructorRestrictedSerializableState.Create("created-by-orleans-activator");
            var activator = new RecordingActivator(activatedState);
            var activatorProvider = new RecordingActivatorProvider(activator);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddOptions<ClusterOptions>().Configure(options =>
            {
                options.ClusterId = "activation-regression";
                options.ServiceId = $"activation-{Guid.NewGuid():N}";
            });
            services.AddSingleton<IDbContextFactory<TestGrainStateDbContext>>(factory);
            services.AddSingleton<IEFGrainStorageETagConverter<Guid>, GuidGrainStorageETagConverter>();
            services.AddSingleton<IActivatorProvider>(activatorProvider);
            services.AddSingleton<IGrainStorageSerializer, StubGrainStorageSerializer>();
            var serviceProvider = services.BuildServiceProvider();
            var storage = EFStorageFactory.Create<TestGrainStateDbContext, Guid>(
                serviceProvider,
                StorageName);

            return new ActivationFixture(
                connection,
                factory,
                serviceProvider,
                storage,
                activatedState,
                activatorProvider,
                activator);
        }

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<TestGrainStateDbContext> options)
        : IDbContextFactory<TestGrainStateDbContext>
    {
        public TestGrainStateDbContext CreateDbContext() => new(options);
    }

    private sealed class TestGrainStateDbContext(DbContextOptions<TestGrainStateDbContext> options)
        : GuidGrainStateDbContext<TestGrainStateDbContext>(options);

    private sealed class RecordingActivatorProvider(RecordingActivator activator) : IActivatorProvider
    {
        public int GetActivatorCallCount { get; private set; }

        public IActivator<T> GetActivator<T>()
        {
            GetActivatorCallCount++;
            Assert.Equal(typeof(ConstructorRestrictedSerializableState), typeof(T));
            return (IActivator<T>)(object)activator;
        }
    }

    private sealed class RecordingActivator(ConstructorRestrictedSerializableState instance)
        : IActivator<ConstructorRestrictedSerializableState>
    {
        public int CreateCallCount { get; private set; }

        public ConstructorRestrictedSerializableState Create()
        {
            CreateCallCount++;
            return instance;
        }
    }

    private sealed class StubGrainStorageSerializer : IGrainStorageSerializer
    {
        public BinaryData Serialize<T>(T? input) => new([1, 2, 3]);

        public T? Deserialize<T>(BinaryData input) => throw new NotSupportedException();
    }
}
