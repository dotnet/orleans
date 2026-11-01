using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;
using Orleans.Hosting;
using Orleans.Persistence.TestKit;
using Orleans.Persistence.MongoDB;
using Orleans.Providers.MongoDB.StorageProviders.Serializers;
using Orleans.Runtime;
using Orleans.Serialization.Activators;
using Orleans.Serialization.Configuration;
using Orleans.Storage;
using Xunit;

namespace Orleans.MongoDB.Tests.Persistence;

public enum MongoSerializerKind { Json, Bson, Binary }

[TestProvider("MongoDB"), TestSuite("BVT"), TestArea("Persistence")]
public sealed class MongoJsonGrainStorageTests(ITestOutputHelper output) : MongoGrainStorageTestsBase(output)
{
    protected override MongoSerializerKind SerializerKind => MongoSerializerKind.Json;
    [Fact] public Task LegacyRawDocument_NullEtagReadAndUpgradePreserveQueryableState() => CheckLegacyRawDocument();
    [Fact] public Task LegacyRawDocument_NullEtagClearRemovesOnlyTargetAndResetsState() => CheckLegacyRawClear();
}

[TestProvider("MongoDB"), TestSuite("BVT"), TestArea("Persistence")]
public sealed class MongoBsonGrainStorageTests(ITestOutputHelper output) : MongoGrainStorageTestsBase(output)
{
    protected override MongoSerializerKind SerializerKind => MongoSerializerKind.Bson;
    [Fact] public Task LegacyRawDocument_NullEtagReadAndUpgradePreserveQueryableState() => CheckLegacyRawDocument();
    [Fact] public Task LegacyRawDocument_NullEtagClearRemovesOnlyTargetAndResetsState() => CheckLegacyRawClear();
}

[TestProvider("MongoDB"), TestSuite("BVT"), TestArea("Persistence")]
public sealed class MongoBinaryGrainStorageTests(ITestOutputHelper output) : MongoGrainStorageTestsBase(output)
{
    protected override MongoSerializerKind SerializerKind => MongoSerializerKind.Binary;
}

public abstract class MongoGrainStorageTestsBase(ITestOutputHelper output) : IAsyncLifetime
{
    private MongoDatabase? database;
    private IHost? host;
    private StorageRunner? runner;
    private const string ProviderName = "MongoTestStorage";
    protected abstract MongoSerializerKind SerializerKind { get; }
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private IGrainStorage Storage
    {
        get
        {
            MongoDatabase.RequireConnection();
            return host!.Services.GetRequiredKeyedService<IGrainStorage>(ProviderName);
        }
    }
    private StorageRunner Runner
    {
        get
        {
            MongoDatabase.RequireConnection();
            return runner!;
        }
    }

    public async ValueTask InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(MongoDatabase.ConnectionVariable))) return;
        database = await MongoDatabase.CreateAsync();
        try
        {
            host = MongoTestHost.Create(silo =>
            {
                silo.Services.AddSingleton<IMongoClient>(database.Client);
                silo.Services.AddSingleton<MongoActivatedStateActivator>();
                silo.Services.Configure<TypeManifestOptions>(options =>
                    options.AddActivator(typeof(MongoActivatedStateActivator), typeof(MongoActivatedState)));
                if (SerializerKind == MongoSerializerKind.Bson)
                    silo.Services.AddKeyedSingleton<IGrainStateSerializer, BsonGrainStateSerializer>(ProviderName);
                else if (SerializerKind == MongoSerializerKind.Binary)
                    silo.Services.AddKeyedSingleton<IGrainStateSerializer, BinaryGrainStateSerializer>(ProviderName);
                // JSON deliberately uses the production registration's default serializer.
                silo.AddMongoDBGrainStorage(ProviderName, (MongoDBGrainStorageOptions options) =>
                {
                    options.DatabaseName = database.Name;
                    options.CollectionPrefix = "Contract_";
                });
            });
            runner = new StorageRunner(Storage);
        }
        catch
        {
            host?.Dispose();
            await database.DisposeAsync();
            database = null;
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { host?.Dispose(); }
        finally { if (database is not null) await database.DisposeAsync(); }
    }

    private sealed class StorageRunner(IGrainStorage storage) : GrainStorageTestRunner(storage);

    [Fact, TestCategory("ModelBased")]
    public Task PersistenceStorage_ModelBasedGeneratedConformance()
        => new GrainStorageModelBasedTestRunner(Storage, $"MongoDB/{SerializerKind}", output.WriteLine)
            .RunGeneratedConformanceTests(Token);

    [Fact] public Task PersistenceStorage_WriteReadIdCyrillic() => Runner.PersistenceStorage_WriteReadIdCyrillicAsync(Token);
    [Fact] public Task PersistenceStorage_WriteDuplicateFailsWithInconsistentStateException() => Runner.PersistenceStorage_WriteDuplicateFailsWithInconsistentStateExceptionAsync(Token);
    [Fact] public Task PersistenceStorage_WriteInconsistentFailsWithInconsistentStateException() => Runner.PersistenceStorage_WriteInconsistentFailsWithInconsistentStateExceptionAsync(Token);
    [Fact] public Task PersistenceStorage_WriteReadWriteReadStatesInParallel() => Runner.PersistenceStorage_WriteReadWriteReadStatesInParallelAsync(Token);
    [Fact] public Task PersistenceStorage_ReadNonExistentState() => Runner.PersistenceStorage_ReadNonExistentStateAsync(Token);
    [Fact] public Task PersistenceStorage_WriteClearWrite() => Runner.PersistenceStorage_WriteClearWriteAsync(Token);
    [Fact] public Task PersistenceStorage_WriteClearRead() => Runner.PersistenceStorage_WriteClearReadAsync(Token);
    [Fact] public Task PersistenceStorage_WriteRead_StringKey() => Runner.PersistenceStorage_WriteRead_StringKeyAsync(Token);
    [Fact] public Task PersistenceStorage_WriteRead_IntegerKey() => Runner.PersistenceStorage_WriteRead_IntegerKeyAsync(Token);
    [Fact] public Task PersistenceStorage_ETagChangesOnWrite() => Runner.PersistenceStorage_ETagChangesOnWriteAsync(Token);
    [Fact] public Task PersistenceStorage_ClearBeforeWrite() => Runner.PersistenceStorage_ClearBeforeWriteAsync(Token);
    [Fact] public Task PersistenceStorage_ClearStateDoesNotNullifyState() => Runner.PersistenceStorage_ClearStateDoesNotNullifyStateAsync(Token);
    [Fact] public Task PersistenceStorage_ClearUpdatesETag() => Runner.PersistenceStorage_ClearUpdatesETagAsync(Token);
    [Fact] public Task PersistenceStorage_ReadAfterClear() => Runner.PersistenceStorage_ReadAfterClearAsync(Token);
    [Fact] public Task PersistenceStorage_MultipleClearOperations() => Runner.PersistenceStorage_MultipleClearOperationsAsync(Token);
    [Fact] public Task PersistenceStorage_ReadNonExistentStateHasNonNullState() => Runner.PersistenceStorage_ReadNonExistentStateHasNonNullStateAsync(Token);
    [Fact] public Task PersistenceStorage_StateNamesUseIndependentRecords() => Runner.PersistenceStorage_StateNamesUseIndependentRecordsAsync(Token);
    [Fact] public Task PersistenceStorage_ClearInconsistentFailsWithInconsistentStateException() => Runner.PersistenceStorage_ClearInconsistentFailsWithInconsistentStateExceptionAsync(Token);
    [Fact] public Task PersistenceStorage_WriteReadClearReadCycle() => Runner.PersistenceStorage_WriteReadClearReadCycleAsync(Token);
    [Fact] public Task PersistenceStorage_WriteWithSameValuesUpdatesETag() => Runner.PersistenceStorage_WriteWithSameValuesUpdatesETagAsync(Token);

    [Fact]
    public async Task MissingReadAndClear_UseRegisteredActivatorForStateWithoutDefaultConstructor()
    {
        var storage = Storage;
        var activator = host!.Services.GetRequiredService<MongoActivatedStateActivator>();
        var id = GrainId.Create("account", "custom-activator");
        var original = new MongoActivatedState(999) { Name = "stale" };
        var state = new GrainState<MongoActivatedState> { State = original, ETag = "stale", RecordExists = true };
        await storage.ReadStateAsync("Activated", id, state, Token);
        Assert.Equal(1, activator.Calls);
        Assert.NotSame(original, state.State);
        Assert.Equal(73, state.State.Count);
        Assert.Equal("activated", state.State.Name);
        Assert.Null(state.ETag);
        Assert.False(state.RecordExists);
        var activated = state.State;
        state.State.Count = 42;
        state.State.Name = "persisted";
        await storage.WriteStateAsync("Activated", id, state, Token);
        Assert.True(state.RecordExists);
        Assert.False(string.IsNullOrWhiteSpace(state.ETag));
        await storage.ClearStateAsync("Activated", id, state, Token);
        Assert.Equal(2, activator.Calls);
        Assert.NotSame(activated, state.State);
        Assert.Equal(73, state.State.Count);
        Assert.Equal("activated", state.State.Name);
        Assert.Null(state.ETag);
        Assert.False(state.RecordExists);
        Assert.Equal(0, await database!.Database.GetCollection<BsonDocument>("Contract_Activated")
            .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Token));
        var cleared = state.State;
        await storage.ClearStateAsync("Activated", id, state, Token);
        Assert.Equal(3, activator.Calls);
        Assert.NotSame(cleared, state.State);
        Assert.Equal(73, state.State.Count);
        Assert.Equal("activated", state.State.Name);
        Assert.Null(state.ETag);
        Assert.False(state.RecordExists);
    }

    [Fact]
    public async Task NativeDocument_HasExpectedQueryableShapeAndMetadata()
    {
        var storage = Storage;
        var grainId = GrainId.Create("account", "customer/Привет");
        var state = new GrainState<MongoTestState>
        {
            State = new MongoTestState { Name = "Ada", Count = 42, Address = new MongoTestAddress { City = "Zürich" } }
        };
        await storage.WriteStateAsync("Account", grainId, state, Token);
        var collection = database!.Database.GetCollection<BsonDocument>("Contract_Account");
        var native = await collection.Find(new BsonDocument("_id", grainId.ToString())).SingleAsync(Token);
        Assert.True(state.RecordExists);
        Assert.Equal(state.ETag, native["_etag"].AsString);
        Assert.Equal(grainId.ToString(), native["_id"].AsString);
        var document = native["_doc"].AsBsonDocument;
        if (SerializerKind == MongoSerializerKind.Binary)
        {
            Assert.Equal(new[] { "data" }, document.Names);
            Assert.NotEmpty(document["data"].AsByteArray);
        }
        else
        {
            Assert.Equal("Ada", document["Name"].AsString);
            Assert.Equal(42, document["Count"].ToInt32());
            Assert.Equal("Zürich", document["Address"]["City"].AsString);
            Assert.False(document.Contains("data"));
            Assert.Equal(1, await collection.CountDocumentsAsync(
                new BsonDocument { { "_doc.Count", 42 }, { "_doc.Address.City", "Zürich" } }, cancellationToken: Token));
        }
        var read = new GrainState<MongoTestState> { State = new MongoTestState() };
        await storage.ReadStateAsync("Account", grainId, read, Token);
        Assert.Equal("Ada", read.State.Name);
        Assert.Equal(42, read.State.Count);
        Assert.Equal("Zürich", read.State.Address.City);
        Assert.Equal(state.ETag, read.ETag);
        Assert.True(read.RecordExists);
    }

    [Fact]
    public async Task StaleWriteAndClear_LeaveStoredAndCallerStateUnchanged()
    {
        var storage = Storage;
        var id = GrainId.Create("account", "etag-regression");
        var current = new GrainState<MongoTestState> { State = new MongoTestState { Count = 10, Name = "first" } };
        await storage.WriteStateAsync("Account", id, current, Token);
        var stale = new GrainState<MongoTestState>
        {
            State = new MongoTestState { Count = 999, Name = "stale" },
            ETag = current.ETag,
            RecordExists = true
        };
        var staleEtag = stale.ETag;
        current.State.Count = 20;
        current.State.Name = "committed";
        await storage.WriteStateAsync("Account", id, current, Token);
        Assert.NotEqual(staleEtag, current.ETag);
        var writeError = await Assert.ThrowsAsync<InconsistentStateException>(() => storage.WriteStateAsync("Account", id, stale, Token));
        var clearError = await Assert.ThrowsAsync<InconsistentStateException>(() => storage.ClearStateAsync("Account", id, stale, Token));
        foreach (var exception in new[] { writeError, clearError })
        {
            Assert.Equal(current.ETag, exception.StoredEtag);
            Assert.Equal(staleEtag, exception.CurrentEtag);
        }
        Assert.Equal(staleEtag, stale.ETag);
        Assert.True(stale.RecordExists);
        Assert.Equal(999, stale.State.Count);
        var read = new GrainState<MongoTestState> { State = new MongoTestState() };
        await storage.ReadStateAsync("Account", id, read, Token);
        Assert.Equal(20, read.State.Count);
        Assert.Equal("committed", read.State.Name);
        Assert.Equal(current.ETag, read.ETag);
        Assert.True(read.RecordExists);

        var committedEtag = current.ETag;
        await storage.ClearStateAsync("Account", id, current, Token);
        Assert.False(current.RecordExists);
        Assert.NotEqual(committedEtag, current.ETag);
        Assert.NotNull(current.State);
        Assert.Equal(0, await database!.Database.GetCollection<BsonDocument>("Contract_Account")
            .CountDocumentsAsync(new BsonDocument("_id", id.ToString()), cancellationToken: Token));
    }

    [Fact]
    public async Task WildcardWriteAndClear_OverwriteOrDeleteRegardlessOfEtag()
    {
        var storage = Storage;
        var id = GrainId.Create("account", "wildcard");
        var state = new GrainState<MongoTestState>
        {
            State = new MongoTestState { Name = "created", Count = 10 },
            ETag = "*"
        };
        await storage.WriteStateAsync("Account", id, state, Token);
        Assert.True(state.RecordExists);
        Assert.False(string.IsNullOrWhiteSpace(state.ETag));
        Assert.NotEqual("*", state.ETag);
        var createdTag = state.ETag;
        var overwrite = new GrainState<MongoTestState>
        {
            State = new MongoTestState { Name = "overwritten", Count = 20 },
            ETag = "*"
        };
        await storage.WriteStateAsync("Account", id, overwrite, Token);
        Assert.True(overwrite.RecordExists);
        Assert.NotEqual(createdTag, overwrite.ETag);
        Assert.NotEqual("*", overwrite.ETag);
        var read = new GrainState<MongoTestState> { State = new MongoTestState() };
        await storage.ReadStateAsync("Account", id, read, Token);
        Assert.Equal("overwritten", read.State.Name);
        Assert.Equal(20, read.State.Count);
        Assert.Equal(overwrite.ETag, read.ETag);
        Assert.True(read.RecordExists);
        state.ETag = "*";
        await storage.ClearStateAsync("Account", id, state, Token);
        Assert.Null(state.ETag);
        Assert.False(state.RecordExists);
        Assert.Equal(0, await database!.Database.GetCollection<BsonDocument>("Contract_Account")
            .CountDocumentsAsync(new BsonDocument("_id", id.ToString()), cancellationToken: Token));
        state.ETag = "*";
        state.RecordExists = true;
        await storage.ClearStateAsync("Account", id, state, Token);
        Assert.Null(state.ETag);
        Assert.False(state.RecordExists);
    }

    [Fact]
    public async Task DeletedRow_StaleWriteThrowsWithoutRecreatingRecordOrChangingCallerMetadata()
    {
        var storage = Storage;
        var id = GrainId.Create("account", "deleted");
        var current = new GrainState<MongoTestState> { State = new MongoTestState { Name = "committed", Count = 10 } };
        await storage.WriteStateAsync("Account", id, current, Token);
        var stale = new GrainState<MongoTestState>
        {
            State = new MongoTestState { Name = "stale", Count = 999 },
            ETag = current.ETag,
            RecordExists = false
        };
        var staleTag = stale.ETag;
        await storage.ClearStateAsync("Account", id, current, Token);
        var exception = await Assert.ThrowsAsync<InconsistentStateException>(() => storage.WriteStateAsync("Account", id, stale, Token));
        Assert.Null(exception.StoredEtag);
        Assert.Equal(staleTag, exception.CurrentEtag);
        Assert.Equal(staleTag, stale.ETag);
        Assert.False(stale.RecordExists);
        Assert.Equal("stale", stale.State.Name);
        Assert.Equal(999, stale.State.Count);
        Assert.Equal(0, await database!.Database.GetCollection<BsonDocument>("Contract_Account")
            .CountDocumentsAsync(new BsonDocument("_id", id.ToString()), cancellationToken: Token));
        // Clearing an already absent row is idempotent even when the caller retained the old token.
        await storage.ClearStateAsync("Account", id, stale, Token);
        Assert.Null(stale.ETag);
        Assert.False(stale.RecordExists);
    }

    [Fact]
    public async Task NullEtagDuplicateWrite_DoesNotChangeMetadataOrOverwriteRecord()
    {
        var storage = Storage;
        var id = GrainId.Create("account", "null-etag");
        var current = new GrainState<MongoTestState> { State = new MongoTestState { Name = "committed", Count = 10 } };
        await storage.WriteStateAsync("Account", id, current, Token);
        var duplicate = new GrainState<MongoTestState> { State = new MongoTestState { Name = "duplicate", Count = 999 } };
        var exception = await Assert.ThrowsAsync<InconsistentStateException>(() => storage.WriteStateAsync("Account", id, duplicate, Token));
        Assert.Equal(current.ETag, exception.StoredEtag);
        Assert.Null(exception.CurrentEtag);
        Assert.Null(duplicate.ETag);
        Assert.False(duplicate.RecordExists);
        Assert.Equal("duplicate", duplicate.State.Name);
        Assert.Equal(999, duplicate.State.Count);
        var read = new GrainState<MongoTestState> { State = new MongoTestState() };
        await storage.ReadStateAsync("Account", id, read, Token);
        Assert.True(read.RecordExists);
        Assert.Equal(current.ETag, read.ETag);
        Assert.Equal("committed", read.State.Name);
        Assert.Equal(10, read.State.Count);
    }

    protected async Task CheckLegacyRawDocument()
    {
        var storage = Storage;
        var id = GrainId.Create("account", "legacy-raw");
        var collection = database!.Database.GetCollection<BsonDocument>("Contract_Account");
        await collection.InsertOneAsync(new BsonDocument
        {
            { "_id", id.ToString() }, { "Name", "legacy" }, { "Count", 42 },
            { "Address", new BsonDocument("City", "Zürich") }
        }, cancellationToken: Token);
        var state = new GrainState<MongoTestState> { State = new MongoTestState { Name = "stale" }, ETag = "stale" };
        await storage.ReadStateAsync("Account", id, state, Token);
        Assert.True(state.RecordExists);
        Assert.Null(state.ETag);
        Assert.Equal("legacy", state.State.Name);
        Assert.Equal(42, state.State.Count);
        Assert.Equal("Zürich", state.State.Address.City);
        state.State.Name = "upgraded";
        state.State.Count = 43;
        await storage.WriteStateAsync("Account", id, state, Token);
        Assert.True(state.RecordExists);
        Assert.False(string.IsNullOrWhiteSpace(state.ETag));
        var native = await collection.Find(new BsonDocument("_id", id.ToString())).SingleAsync(Token);
        Assert.Equal(state.ETag, native["_etag"].AsString);
        Assert.Equal("upgraded", native["_doc"]["Name"].AsString);
        Assert.Equal(43, native["_doc"]["Count"].ToInt32());
        var read = new GrainState<MongoTestState> { State = new MongoTestState() };
        await storage.ReadStateAsync("Account", id, read, Token);
        Assert.Equal("upgraded", read.State.Name);
        Assert.Equal(43, read.State.Count);
        Assert.Equal("Zürich", read.State.Address.City);
        Assert.Equal(state.ETag, read.ETag);
        Assert.True(read.RecordExists);
    }

    protected async Task CheckLegacyRawClear()
    {
        var storage = Storage;
        var id = GrainId.Create("account", "raw-clear");
        var neighbor = GrainId.Create("account", "raw-neighbor");
        var collection = database!.Database.GetCollection<BsonDocument>("Contract_Account");
        await collection.InsertManyAsync(
        [
            new BsonDocument { { "_id", id.ToString() }, { "Name", "legacy" }, { "Count", 42 } },
            new BsonDocument { { "_id", neighbor.ToString() }, { "Name", "neighbor" }, { "Count", 91 } }
        ], cancellationToken: Token);
        var state = new GrainState<MongoTestState> { State = new MongoTestState(), ETag = "stale" };
        await storage.ReadStateAsync("Account", id, state, Token);
        Assert.True(state.RecordExists);
        Assert.Null(state.ETag);
        Assert.Equal(42, state.State.Count);
        Assert.Equal("legacy", state.State.Name);
        await storage.ClearStateAsync("Account", id, state, Token);
        Assert.Null(state.ETag);
        Assert.False(state.RecordExists);
        Assert.Equal(0, state.State.Count);
        Assert.Equal("", state.State.Name);
        var remaining = await collection.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync(Token);
        Assert.Equal(neighbor.ToString(), remaining["_id"].AsString);
        Assert.Equal(91, remaining["Count"].ToInt32());
        Assert.Equal("neighbor", remaining["Name"].AsString);
        Assert.False(remaining.Contains("_etag"));
    }
}

[GenerateSerializer]
public sealed class MongoActivatedState(int count)
{
    [Id(0)] public int Count { get; set; } = count;
    [Id(1)] public string Name { get; set; } = "";
}

public sealed class MongoActivatedStateActivator : IActivator<MongoActivatedState>
{
    public int Calls { get; private set; }
    public MongoActivatedState Create()
    {
        Calls++;
        return new MongoActivatedState(73) { Name = "activated" };
    }
}

[GenerateSerializer]
public sealed class MongoTestState
{
    [Id(0)] public string Name { get; set; } = "";
    [Id(1)] public int Count { get; set; }
    [Id(2)] public MongoTestAddress Address { get; set; } = new();
}

[GenerateSerializer]
public sealed class MongoTestAddress
{
    [Id(0)] public string City { get; set; } = "";
}
