using System.Collections.Concurrent;
using System.Text.Json;
using Cassandra;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Cassandra;
using Orleans.Persistence.Cassandra;
using Orleans.Runtime;
using Orleans.Serialization.Activators;
using Orleans.Serialization.Serializers;
using Orleans.Storage;
using Xunit;

namespace Orleans.Persistence.Cassandra.Tests;

[Collection("CassandraPersistence")]
[TestProvider("Cassandra")]
[TestSuite("Functional")]
[TestCategory("Cassandra")]
[TestCategory("Persistence")]
[TestArea("Persistence")]
public sealed class CassandraPersistenceIntegrationTests : IClassFixture<CassandraPersistenceContainer>
{
    private static int _tableId;
    private readonly CassandraPersistenceContainer _container;

    public CassandraPersistenceIntegrationTests(CassandraPersistenceContainer container)
    {
        container.EnsurePreconditionsMet();
        _container = container;
    }

    [Fact]
    public async Task StartupCreatesOptInSchemaWithStrictNames()
    {
        var table = NewTableName();
        using var storage = await StartStorageAsync(table, createTable: true);

        var columns = await _container.Session.ExecuteAsync(new SimpleStatement(
            $"SELECT column_name, type, kind, position FROM system_schema.columns WHERE keyspace_name = 'orleans' AND table_name = '{table}'"));
        var columnRows = columns.ToList();
        var schema = columnRows.ToDictionary(row => row.GetValue<string>("column_name"), row => row.GetValue<string>("type"));

        Assert.Equal(
            new Dictionary<string, string>
            {
                ["service_id"] = "text",
                ["grain_id"] = "text",
                ["state_name"] = "text",
                ["grain_type"] = "text",
                ["version"] = "bigint",
                ["record_exists"] = "boolean",
                ["state"] = "blob",
                ["updated_at"] = "timestamp"
            },
            schema);

        var keyColumns = columnRows
            .Where(row => row.GetValue<string>("kind") is "partition_key" or "clustering")
            .OrderBy(row => row.GetValue<string>("kind") == "partition_key" ? 0 : 1)
            .ThenBy(row => row.GetValue<int>("position"))
            .Select(row => (
                Name: row.GetValue<string>("column_name"),
                Type: row.GetValue<string>("type"),
                Kind: row.GetValue<string>("kind"),
                Position: row.GetValue<int>("position")))
            .ToArray();
        Assert.Equal(
            new[]
            {
                (Name: "service_id", Type: "text", Kind: "partition_key", Position: 0),
                (Name: "grain_id", Type: "text", Kind: "partition_key", Position: 1),
                (Name: "state_name", Type: "text", Kind: "clustering", Position: 0)
            },
            keyColumns);
    }

    [Fact]
    public async Task ExistingSchemaAndNumericBigintEtagsAreCompatible()
    {
        var table = NewTableName();
        await CreateTableAsync(table);
        const string serviceId = "existing-schema";
        var grainId = GrainId.Create("compat", "existing");
        var stateName = "state";
        var existing = new TestState(7);
        await InsertRawAsync(table, serviceId, grainId, stateName, version: 41, recordExists: true, existing);

        using var storage = await StartStorageAsync(table, serviceId);
        var loaded = new GrainState<TestState>(new());
        await storage.ReadStateAsync(stateName, grainId, loaded);

        Assert.True(loaded.RecordExists);
        Assert.Equal("41", loaded.ETag);
        Assert.Equal(7, Assert.IsType<TestState>(loaded.State).Value);

        loaded.State = new TestState(8);
        await storage.WriteStateAsync(stateName, grainId, loaded);
        Assert.Equal("42", loaded.ETag);

        var raw = await ReadRawAsync(table, serviceId, grainId, stateName);
        Assert.NotNull(raw);
        Assert.Equal(42L, raw.GetValue<long>("version"));
        Assert.True(raw.GetValue<bool>("record_exists"));
        Assert.Equal(8, Deserialize<TestState>(raw.GetValue<byte[]>("state")).Value);
    }

    [Fact]
    public async Task FirstInsertVersionQualifiedUpdateAndStaleUpdateConflict()
    {
        var table = NewTableName();
        using var storage = await StartStorageAsync(table, createTable: true);
        var grainId = GrainId.Create("versions", Guid.NewGuid().ToString("N"));
        var state = new GrainState<TestState>(new(1));

        await storage.WriteStateAsync("state", grainId, state);
        Assert.Equal("1", state.ETag);
        Assert.Equal(grainId.Type.ToString(), (await ReadRawAsync(table, "cassandra-persistence-tests", grainId, "state"))!.GetValue<string>("grain_type"));

        state.State = new(2);
        await storage.WriteStateAsync("state", grainId, state);
        Assert.Equal("2", state.ETag);

        var stale = new GrainState<TestState>(new(3)) { ETag = "1" };
        await Assert.ThrowsAsync<InconsistentStateException>(
            () => storage.WriteStateAsync("state", grainId, stale));
    }

    [Fact]
    public async Task LogicalClearAdvancesVersionAndPhysicalClearRemovesRow()
    {
        var logicalTable = NewTableName();
        using (var storage = await StartStorageAsync(logicalTable, createTable: true))
        {
            var grainId = GrainId.Create("clear", Guid.NewGuid().ToString("N"));
            var state = new GrainState<TestState>(new(1));
            await storage.WriteStateAsync("state", grainId, state);
            await storage.ClearStateAsync("state", grainId, state);

            Assert.Equal("2", state.ETag);
            var read = new GrainState<TestState>(new(9));
            await storage.ReadStateAsync("state", grainId, read);
            Assert.False(read.RecordExists);
            Assert.Equal("2", read.ETag);

            read.State = new(2);
            await storage.WriteStateAsync("state", grainId, read);
            Assert.Equal("3", read.ETag);
        }

        var physicalTable = NewTableName();
        using (var storage = await StartStorageAsync(physicalTable, createTable: true, deleteStateOnClear: true))
        {
            var grainId = GrainId.Create("clear", Guid.NewGuid().ToString("N"));
            var state = new GrainState<TestState>(new(1));
            await storage.WriteStateAsync("state", grainId, state);
            await storage.ClearStateAsync("state", grainId, state);
            Assert.Null(state.ETag);

            var read = new GrainState<TestState>(new(9));
            await storage.ReadStateAsync("state", grainId, read);
            Assert.False(read.RecordExists);
            Assert.Null(read.ETag);
        }
    }

    [Fact]
    public async Task ClearWithoutEtagHandlesMissingActiveAndRetainedRows()
    {
        var logicalTable = NewTableName();
        using (var storage = await StartStorageAsync(logicalTable, createTable: true))
        {
            var missing = new GrainState<TestState>(new());
            var missingGrainId = GrainId.Create("clear", "missing");
            await storage.ClearStateAsync("state", missingGrainId, missing);
            Assert.Null(missing.ETag);
            Assert.Null(await ReadRawAsync(logicalTable, "cassandra-persistence-tests", missingGrainId, "state"));

            var grainId = GrainId.Create("clear", "active");
            var active = new GrainState<TestState>(new(1));
            await storage.WriteStateAsync("state", grainId, active);
            var conflicting = new GrainState<TestState>(new());
            await Assert.ThrowsAsync<InconsistentStateException>(
                () => storage.ClearStateAsync("state", grainId, conflicting));
            var activeRow = await ReadRawAsync(logicalTable, "cassandra-persistence-tests", grainId, "state");
            Assert.NotNull(activeRow);
            Assert.Equal(1L, activeRow.GetValue<long>("version"));
            Assert.True(activeRow.GetValue<bool>("record_exists"));
            Assert.Equal(1, Deserialize<TestState>(activeRow.GetValue<byte[]>("state")).Value);

            await storage.ClearStateAsync("state", grainId, active);
            var retained = new GrainState<TestState>(new());
            await storage.ClearStateAsync("state", grainId, retained);
            Assert.Equal("2", retained.ETag);
            var tombstone = await ReadRawAsync(logicalTable, "cassandra-persistence-tests", grainId, "state");
            Assert.NotNull(tombstone);
            Assert.Equal(2L, tombstone.GetValue<long>("version"));
            Assert.False(tombstone.GetValue<bool>("record_exists"));
        }

        var physicalTable = NewTableName();
        using (var storage = await StartStorageAsync(physicalTable, createTable: true, deleteStateOnClear: true))
        {
            var missing = new GrainState<TestState>(new());
            var missingGrainId = GrainId.Create("clear", "physical-missing");
            await storage.ClearStateAsync("state", missingGrainId, missing);
            Assert.Null(missing.ETag);
            Assert.Null(await ReadRawAsync(physicalTable, "cassandra-persistence-tests", missingGrainId, "state"));

            var grainId = GrainId.Create("clear", "physical-active");
            var active = new GrainState<TestState>(new(1));
            await storage.WriteStateAsync("state", grainId, active);
            var conflicting = new GrainState<TestState>(new());
            await Assert.ThrowsAsync<InconsistentStateException>(
                () => storage.ClearStateAsync("state", grainId, conflicting));

            var read = new GrainState<TestState>(new());
            await storage.ReadStateAsync("state", grainId, read);
            Assert.True(read.RecordExists);
            Assert.Equal("1", read.ETag);
            Assert.Equal(1, Assert.IsType<TestState>(read.State).Value);
            var activeRow = await ReadRawAsync(physicalTable, "cassandra-persistence-tests", grainId, "state");
            Assert.NotNull(activeRow);
            Assert.Equal(1L, activeRow.GetValue<long>("version"));
            Assert.True(activeRow.GetValue<bool>("record_exists"));
            Assert.Equal(1, Deserialize<TestState>(activeRow.GetValue<byte[]>("state")).Value);
        }

        var retainedTable = NewTableName();
        using (var retainingStorage = await StartStorageAsync(retainedTable, createTable: true))
        {
            var grainId = GrainId.Create("clear", "physical-retained");
            var state = new GrainState<TestState>(new(1));
            await retainingStorage.WriteStateAsync("state", grainId, state);
            await retainingStorage.ClearStateAsync("state", grainId, state);
            Assert.Equal("2", state.ETag);
            Assert.NotNull(await ReadRawAsync(retainedTable, "cassandra-persistence-tests", grainId, "state"));
        }

        using (var deletingStorage = await StartStorageAsync(retainedTable, deleteStateOnClear: true))
        {
            var grainId = GrainId.Create("clear", "physical-retained");
            var freshState = new GrainState<TestState>(new());
            await deletingStorage.ClearStateAsync("state", grainId, freshState);
            Assert.Null(freshState.ETag);
            Assert.Null(await ReadRawAsync(retainedTable, "cassandra-persistence-tests", grainId, "state"));
        }
    }

    [Fact]
    public async Task ContendedInsertAllowsOnlyOneLwtWriter()
    {
        var table = NewTableName();
        using var first = await StartStorageAsync(table, createTable: true);
        using var second = await StartStorageAsync(table);
        var grainId = GrainId.Create("lwt", Guid.NewGuid().ToString("N"));
        var states = new[]
        {
            new GrainState<TestState>(new(1)),
            new GrainState<TestState>(new(2))
        };

        var results = await Task.WhenAll(
            AttemptWriteAsync(first, states[0], grainId),
            AttemptWriteAsync(second, states[1], grainId));

        Assert.Equal(1, results.Count(static success => success));
        Assert.Equal(1, results.Count(static success => !success));
        var read = new GrainState<TestState>(new());
        await first.ReadStateAsync("state", grainId, read);
        Assert.True(read.RecordExists);
        Assert.Equal("1", read.ETag);
        Assert.Contains(Assert.IsType<TestState>(read.State).Value, new[] { 1, 2 });
    }

    [Fact]
    public async Task SameTableSeparatesStateByServiceId()
    {
        var table = NewTableName();
        using var first = await StartStorageAsync(table, serviceId: "service-one", createTable: true);
        using var second = await StartStorageAsync(table, serviceId: "service-two");
        var grainId = GrainId.Create("isolation", "same-grain");

        var firstState = new GrainState<TestState>(new(1));
        var secondState = new GrainState<TestState>(new(2));
        await first.WriteStateAsync("state", grainId, firstState);
        await second.WriteStateAsync("state", grainId, secondState);

        var firstRead = new GrainState<TestState>(new());
        var secondRead = new GrainState<TestState>(new());
        await first.ReadStateAsync("state", grainId, firstRead);
        await second.ReadStateAsync("state", grainId, secondRead);
        Assert.Equal(1, Assert.IsType<TestState>(firstRead.State).Value);
        Assert.Equal(2, Assert.IsType<TestState>(secondRead.State).Value);
        Assert.Equal(1L, (await ReadRawAsync(table, "service-one", grainId, "state"))!.GetValue<long>("version"));
        Assert.Equal(1L, (await ReadRawAsync(table, "service-two", grainId, "state"))!.GetValue<long>("version"));
    }

    [Fact]
    public async Task ContendedVersionQualifiedUpdatesAllowOnlyOneWriter()
    {
        var table = NewTableName();
        using var firstSession = await StartSessionAsync();
        using var first = await StartStorageAsync(table, session: firstSession, createTable: true);
        using var secondSession = _container.OpenSession();
        using var second = await StartStorageAsync(table, session: secondSession);
        var grainId = GrainId.Create("lwt-update", Guid.NewGuid().ToString("N"));

        var initial = new GrainState<TestState>(new(1));
        await first.WriteStateAsync("state", grainId, initial);
        var firstUpdate = new GrainState<TestState>(new());
        var secondUpdate = new GrainState<TestState>(new());
        await first.ReadStateAsync("state", grainId, firstUpdate);
        await second.ReadStateAsync("state", grainId, secondUpdate);
        firstUpdate.State = new(2);
        secondUpdate.State = new(3);

        using var barrier = new Barrier(2);
        var results = await Task.WhenAll(
            Task.Run(() => AttemptAfterBarrierAsync(barrier, () => first.WriteStateAsync("state", grainId, firstUpdate))),
            Task.Run(() => AttemptAfterBarrierAsync(barrier, () => second.WriteStateAsync("state", grainId, secondUpdate))));

        Assert.Equal(1, results.Count(static success => success));
        Assert.Equal(1, results.Count(static success => !success));
        var winningUpdate = results[0] ? firstUpdate : secondUpdate;
        var losingUpdate = results[0] ? secondUpdate : firstUpdate;
        Assert.Equal("2", winningUpdate.ETag);
        Assert.Equal("1", losingUpdate.ETag);
        var row = await ReadRawAsync(table, "cassandra-persistence-tests", grainId, "state");
        Assert.NotNull(row);
        Assert.Equal(2L, row.GetValue<long>("version"));
        Assert.True(row.GetValue<bool>("record_exists"));
        Assert.Contains(Deserialize<TestState>(row.GetValue<byte[]>("state")).Value, new[] { 2, 3 });
    }

    [Fact]
    public async Task ContendedVersionQualifiedClearsAllowOnlyOneWriter()
    {
        var table = NewTableName();
        using var firstSession = await StartSessionAsync();
        using var first = await StartStorageAsync(table, session: firstSession, createTable: true);
        using var secondSession = _container.OpenSession();
        using var second = await StartStorageAsync(table, session: secondSession);
        var grainId = GrainId.Create("lwt-clear", Guid.NewGuid().ToString("N"));

        var initial = new GrainState<TestState>(new(1));
        await first.WriteStateAsync("state", grainId, initial);
        var firstClear = new GrainState<TestState>(new());
        var secondClear = new GrainState<TestState>(new());
        await first.ReadStateAsync("state", grainId, firstClear);
        await second.ReadStateAsync("state", grainId, secondClear);

        using var barrier = new Barrier(2);
        var results = await Task.WhenAll(
            Task.Run(() => AttemptAfterBarrierAsync(barrier, () => first.ClearStateAsync("state", grainId, firstClear))),
            Task.Run(() => AttemptAfterBarrierAsync(barrier, () => second.ClearStateAsync("state", grainId, secondClear))));

        Assert.Equal(1, results.Count(static success => success));
        Assert.Equal(1, results.Count(static success => !success));
        var winningClear = results[0] ? firstClear : secondClear;
        var losingClear = results[0] ? secondClear : firstClear;
        Assert.Equal("2", winningClear.ETag);
        Assert.Equal("1", losingClear.ETag);
        var row = await ReadRawAsync(table, "cassandra-persistence-tests", grainId, "state");
        Assert.NotNull(row);
        Assert.Equal(2L, row.GetValue<long>("version"));
        Assert.False(row.GetValue<bool>("record_exists"));
    }

    [Fact]
    public async Task ExternalSessionRemainsUsableAfterProviderDisposal()
    {
        var table = NewTableName();
        var storage = await StartStorageAsync(table, createTable: true);
        storage.Dispose();

        var result = await _container.Session.ExecuteAsync(new SimpleStatement(
            $"SELECT COUNT(*) FROM orleans.{table}"));
        Assert.Equal(0L, result.First().GetValue<long>("count"));
    }

    [Fact]
    public async Task NamedProvidersUseSeparateTables()
    {
        var firstTable = NewTableName();
        var secondTable = NewTableName();
        using var first = await StartStorageAsync(firstTable, createTable: true, providerName: "First");
        using var second = await StartStorageAsync(secondTable, createTable: true, providerName: "Second");
        var grainId = GrainId.Create("isolation", "same-grain");

        var firstState = new GrainState<TestState>(new(1));
        var secondState = new GrainState<TestState>(new(2));
        await first.WriteStateAsync("state", grainId, firstState);
        await second.WriteStateAsync("state", grainId, secondState);

        var firstRead = new GrainState<TestState>(new());
        var secondRead = new GrainState<TestState>(new());
        await first.ReadStateAsync("state", grainId, firstRead);
        await second.ReadStateAsync("state", grainId, secondRead);
        Assert.Equal(1, Assert.IsType<TestState>(firstRead.State).Value);
        Assert.Equal(2, Assert.IsType<TestState>(secondRead.State).Value);
    }

    private async Task<CassandraGrainStorage> StartStorageAsync(
        string table,
        string serviceId = "cassandra-persistence-tests",
        bool createTable = false,
        bool deleteStateOnClear = false,
        string providerName = "Cassandra",
        ISession? session = null)
    {
        await _container.EnsureStartedAsync(TestContext.Current.CancellationToken);
        var options = new CassandraGrainStorageOptions
        {
            Keyspace = "orleans",
            TableName = table,
            CreateTableIfNotExists = createTable,
            DeleteStateOnClear = deleteStateOnClear,
            GrainStorageSerializer = new JsonStorageSerializer()
        };
        options.ConfigureClient(_ => Task.FromResult(session ?? _container.Session));
        var services = new ServiceCollection().BuildServiceProvider();
        var storage = new CassandraGrainStorage(
            providerName,
            options,
            Options.Create(new ClusterOptions { ServiceId = serviceId }),
            new TestActivatorProvider(services),
            options.GrainStorageSerializer,
            NullLogger<CassandraGrainStorage>.Instance,
            services);
        var lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
        storage.Participate(lifecycle);
        await lifecycle.OnStart(TestContext.Current.CancellationToken);
        return storage;
    }

    private async Task<ISession> StartSessionAsync()
    {
        await _container.EnsureStartedAsync(TestContext.Current.CancellationToken);
        return _container.OpenSession();
    }

    private async Task CreateTableAsync(string table)
    {
        await _container.EnsureStartedAsync(TestContext.Current.CancellationToken);
        await _container.Session.ExecuteAsync(new SimpleStatement($"""
            CREATE TABLE {CassandraIdentifier.Quote(table)}
            (
                service_id text,
                grain_id text,
                state_name text,
                grain_type text,
                version bigint,
                record_exists boolean,
                state blob,
                updated_at timestamp,
                PRIMARY KEY ((service_id, grain_id), state_name)
            )
            """));
    }

    private async Task InsertRawAsync(
        string table,
        string serviceId,
        GrainId grainId,
        string stateName,
        long version,
        bool recordExists,
        TestState state)
    {
        var prepared = await _container.Session.PrepareAsync(
            $"INSERT INTO {CassandraIdentifier.Quote(table)} (service_id, grain_id, state_name, grain_type, version, record_exists, state, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?)");
        await _container.Session.ExecuteAsync(prepared.Bind(
            serviceId,
            grainId.ToString(),
            stateName,
            typeof(TestState).FullName!,
            version,
            recordExists,
            SerializeValue(state),
            DateTimeOffset.UtcNow));
    }

    private async Task<Row?> ReadRawAsync(string table, string serviceId, GrainId grainId, string stateName)
    {
        var prepared = await _container.Session.PrepareAsync(
            $"SELECT grain_type, version, record_exists, state FROM {CassandraIdentifier.Quote(table)} WHERE service_id = ? AND grain_id = ? AND state_name = ?");
        return (await _container.Session.ExecuteAsync(prepared.Bind(serviceId, grainId.ToString(), stateName))).FirstOrDefault();
    }

    private static async Task<bool> AttemptAfterBarrierAsync(Barrier barrier, Func<Task> operation)
    {
        barrier.SignalAndWait(TestContext.Current.CancellationToken);
        return await AttemptAsync(operation);
    }

    private static async Task<bool> AttemptAsync(Func<Task> operation)
    {
        try
        {
            await operation();
            return true;
        }
        catch (InconsistentStateException)
        {
            return false;
        }
    }

    private static async Task<bool> AttemptWriteAsync(
        IGrainStorage storage,
        GrainState<TestState> state,
        GrainId grainId)
        => await AttemptAsync(() => storage.WriteStateAsync("state", grainId, state));

    private static string NewTableName() =>
        $"grain_state_{Interlocked.Increment(ref _tableId):x}";

    private static byte[] SerializeValue<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value);

    private static T Deserialize<T>(byte[] value) =>
        JsonSerializer.Deserialize<T>(value)!;

    private sealed class TestState
    {
        public TestState() { }
        public TestState(int value) => Value = value;
        public int Value { get; set; }
    }

    private sealed class JsonStorageSerializer : IGrainStorageSerializer
    {
        public BinaryData Serialize<T>(T? input) => BinaryData.FromBytes(SerializeValue(input));
        public T? Deserialize<T>(BinaryData input) => JsonSerializer.Deserialize<T>(input.ToMemory().Span);
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

[CollectionDefinition("CassandraPersistence", DisableParallelization = true)]
public sealed class CassandraPersistenceCollection : ICollectionFixture<CassandraPersistenceContainer>;
