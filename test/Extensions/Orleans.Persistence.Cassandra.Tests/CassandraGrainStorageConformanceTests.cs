using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Persistence.TestKit;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Activators;
using Orleans.Serialization.Serializers;
using Orleans.Storage;
using TestExtensions;
using Xunit;

namespace Orleans.Persistence.Cassandra.Tests;

[Collection("CassandraPersistence")]
[TestProvider("Cassandra")]
[TestSuite("Functional")]
[TestCategory("Cassandra")]
[TestCategory("Persistence")]
[TestArea("Persistence")]
public sealed class CassandraGrainStorageConformanceTests
    : GrainStorageTestRunner, IClassFixture<CassandraGrainStorageConformanceTests.Fixture>
{
    public CassandraGrainStorageConformanceTests(Fixture fixture)
        : base(fixture.Storage)
    {
        fixture.Container.EnsurePreconditionsMet();
    }

    [Fact]
    public override Task PersistenceStorage_WriteReadIdCyrillic() =>
        base.PersistenceStorage_WriteReadIdCyrillicAsync(TestContext.Current.CancellationToken);

    [Fact]
    public override Task PersistenceStorage_WriteDuplicateFailsWithInconsistentStateException() =>
        base.PersistenceStorage_WriteDuplicateFailsWithInconsistentStateExceptionAsync(TestContext.Current.CancellationToken);

    [Fact]
    public override Task PersistenceStorage_WriteInconsistentFailsWithInconsistentStateException() =>
        base.PersistenceStorage_WriteInconsistentFailsWithInconsistentStateExceptionAsync(TestContext.Current.CancellationToken);

    [Fact]
    public override Task PersistenceStorage_WriteReadWriteReadStatesInParallel() =>
        RunPersistenceStorage_WriteReadWriteReadStatesInParallel(
            "CassandraConformance",
            50,
            TestContext.Current.CancellationToken);

    [Fact]
    public override Task PersistenceStorage_ReadNonExistentState() =>
        base.PersistenceStorage_ReadNonExistentStateAsync(TestContext.Current.CancellationToken);

    [Fact]
    public override Task PersistenceStorage_ReadNonExistentStateHasNonNullState() =>
        base.PersistenceStorage_ReadNonExistentStateHasNonNullStateAsync(TestContext.Current.CancellationToken);

    [Fact]
    public override Task PersistenceStorage_WriteClearWrite() =>
        base.PersistenceStorage_WriteClearWriteAsync(TestContext.Current.CancellationToken);

    [Fact]
    public override Task PersistenceStorage_WriteClearRead() =>
        base.PersistenceStorage_WriteClearReadAsync(TestContext.Current.CancellationToken);

    [Fact]
    public override Task PersistenceStorage_WriteReadClearReadCycle() =>
        base.PersistenceStorage_WriteReadClearReadCycleAsync(TestContext.Current.CancellationToken);

    [Fact]
    public override Task PersistenceStorage_WriteRead_StringKey() =>
        base.PersistenceStorage_WriteRead_StringKeyAsync(TestContext.Current.CancellationToken);

    [Fact]
    public override Task PersistenceStorage_WriteRead_IntegerKey() =>
        base.PersistenceStorage_WriteRead_IntegerKeyAsync(TestContext.Current.CancellationToken);

    [Fact]
    public override Task PersistenceStorage_ETagChangesOnWrite() =>
        base.PersistenceStorage_ETagChangesOnWriteAsync(TestContext.Current.CancellationToken);

    [Fact]
    public override Task PersistenceStorage_ClearBeforeWrite() =>
        base.PersistenceStorage_ClearBeforeWriteAsync(TestContext.Current.CancellationToken);

    [Fact]
    public override Task PersistenceStorage_ClearStateDoesNotNullifyState() =>
        base.PersistenceStorage_ClearStateDoesNotNullifyStateAsync(TestContext.Current.CancellationToken);

    [Fact]
    public override Task PersistenceStorage_ClearUpdatesETag() =>
        base.PersistenceStorage_ClearUpdatesETagAsync(TestContext.Current.CancellationToken);

    [Fact]
    public override Task PersistenceStorage_ReadAfterClear() =>
        base.PersistenceStorage_ReadAfterClearAsync(TestContext.Current.CancellationToken);

    [Fact]
    public override Task PersistenceStorage_MultipleClearOperations() =>
        base.PersistenceStorage_MultipleClearOperationsAsync(TestContext.Current.CancellationToken);

    [Fact]
    public override Task PersistenceStorage_WriteWithSameValuesUpdatesETag() =>
        base.PersistenceStorage_WriteWithSameValuesUpdatesETagAsync(TestContext.Current.CancellationToken);

    [Fact]
    public override Task PersistenceStorage_StateNamesUseIndependentRecords() =>
        base.PersistenceStorage_StateNamesUseIndependentRecordsAsync(TestContext.Current.CancellationToken);

    [Fact]
    public override Task PersistenceStorage_ClearInconsistentFailsWithInconsistentStateException() =>
        base.PersistenceStorage_ClearInconsistentFailsWithInconsistentStateExceptionAsync(TestContext.Current.CancellationToken);

    public sealed class Fixture : IAsyncLifetime
    {
        private CassandraGrainStorage? _storage;

        public CassandraPersistenceContainer Container { get; } = new();

        public IGrainStorage Storage =>
            _storage ?? throw new InvalidOperationException("The Cassandra storage fixture has not been initialized.");

        public async ValueTask InitializeAsync()
        {
            Container.EnsurePreconditionsMet();
            await Container.EnsureStartedAsync(TestContext.Current.CancellationToken);

            var services = new ServiceCollection().BuildServiceProvider();
            var options = new CassandraGrainStorageOptions
            {
                Keyspace = "orleans",
                TableName = $"cassandra_conf_{Guid.NewGuid():N}",
                CreateTableIfNotExists = true,
                GrainStorageSerializer = new JsonStorageSerializer()
            };
            options.ConfigureClient(_ => Task.FromResult(Container.Session));

            _storage = new CassandraGrainStorage(
                "Cassandra",
                options,
                Options.Create(new ClusterOptions { ServiceId = "cassandra-conformance-tests" }),
                new TestActivatorProvider(services),
                options.GrainStorageSerializer,
                NullLogger<CassandraGrainStorage>.Instance,
                services);

            var lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
            _storage.Participate(lifecycle);
            await lifecycle.OnStart(TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            _storage?.Dispose();
            await Container.DisposeAsync();
        }
    }

    private sealed class JsonStorageSerializer : IGrainStorageSerializer
    {
        public BinaryData Serialize<T>(T? input) => BinaryData.FromString(JsonSerializer.Serialize(input));

        public T? Deserialize<T>(BinaryData input) => JsonSerializer.Deserialize<T>(input.ToString());
    }

    private sealed class TestActivatorProvider(IServiceProvider services) : IActivatorProvider
    {
        public IActivator<T> GetActivator<T>() => new TestActivator<T>(services);
    }

    private sealed class TestActivator<T>(IServiceProvider services) : IActivator<T>
    {
        public T Create() => ActivatorUtilities.CreateInstance<T>(services);
    }
}
