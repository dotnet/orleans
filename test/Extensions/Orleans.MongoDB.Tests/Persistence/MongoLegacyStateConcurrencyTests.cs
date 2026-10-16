using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Orleans.Hosting;
using Orleans.Persistence.MongoDB;
using Orleans.Runtime;
using Orleans.Storage;
using Xunit;

namespace Orleans.MongoDB.Tests.Persistence;

[TestProvider("MongoDB"), TestSuite("BVT"), TestArea("Persistence")]
public sealed class MongoLegacyStateConcurrencyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task UnreadRawDocument_InitialWriteCannotOverwriteStoredState(string? etag)
    {
        await using var database = await MongoDatabase.CreateAsync();
        using var host = CreateHost(database);
        var storage = host.Services.GetRequiredKeyedService<IGrainStorage>("store");
        var token = TestContext.Current.CancellationToken;
        var id = GrainId.Create("account", "unread-legacy");
        var collection = database.Database.GetCollection<BsonDocument>("Legacy_Account");
        var raw = new BsonDocument { { "_id", id.ToString() }, { "Name", "unread" }, { "Count", 42 } };
        await collection.InsertOneAsync(raw, cancellationToken: token);
        var original = new MongoTestState { Name = "new-activation", Count = 999 };
        var state = new GrainState<MongoTestState> { State = original, ETag = etag, RecordExists = false };

        var error = await Assert.ThrowsAsync<InconsistentStateException>(() => storage.WriteStateAsync("Account", id, state, token));
        Assert.Null(error.StoredEtag);
        Assert.Equal(etag, error.CurrentEtag);
        Assert.Same(original, state.State);
        Assert.Equal(etag, state.ETag);
        Assert.False(state.RecordExists);
        Assert.Equal(raw, await collection.Find(new BsonDocument("_id", id.ToString())).SingleAsync(token));

        await storage.ReadStateAsync("Account", id, state, token);
        Assert.True(state.RecordExists);
        Assert.Null(state.ETag);
        Assert.Equal("unread", state.State.Name);
        Assert.Equal(42, state.State.Count);
        state.State.Name = "read-before-update";
        state.State.Count = 43;
        await storage.WriteStateAsync("Account", id, state, token);
        Assert.False(string.IsNullOrWhiteSpace(state.ETag));
        var upgraded = await collection.Find(new BsonDocument("_id", id.ToString())).SingleAsync(token);
        Assert.Equal(state.ETag, upgraded["_etag"].AsString);
        Assert.Equal("read-before-update", upgraded["_doc"]["Name"].AsString);
        Assert.Equal(43, upgraded["_doc"]["Count"].AsInt32);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task PreviouslyReadRawDocument_ConcurrentClearCannotBeUndoneByStaleWrite(string? retainedEtag)
    {
        await using var database = await MongoDatabase.CreateAsync();
        using var firstHost = CreateHost(database);
        using var secondHost = CreateHost(database);
        var storage = firstHost.Services.GetRequiredKeyedService<IGrainStorage>("store");
        var otherStorage = secondHost.Services.GetRequiredKeyedService<IGrainStorage>("store");
        var token = TestContext.Current.CancellationToken;
        var id = GrainId.Create("account", "removed-legacy");
        var collection = database.Database.GetCollection<BsonDocument>("Legacy_Account");
        await collection.InsertOneAsync(
            new BsonDocument { { "_id", id.ToString() }, { "Name", "original" }, { "Count", 42 } }, cancellationToken: token);
        var stale = new GrainState<MongoTestState> { State = new MongoTestState() };
        var remover = new GrainState<MongoTestState> { State = new MongoTestState() };
        await storage.ReadStateAsync("Account", id, stale, token);
        await otherStorage.ReadStateAsync("Account", id, remover, token);
        Assert.Null(stale.ETag);
        Assert.True(stale.RecordExists);
        Assert.Null(remover.ETag);
        Assert.True(remover.RecordExists);
        await otherStorage.ClearStateAsync("Account", id, remover, token);
        Assert.False(remover.RecordExists);
        stale.ETag = retainedEtag;
        stale.State.Name = "stale";
        stale.State.Count = 999;
        var original = stale.State;

        var error = await Assert.ThrowsAsync<InconsistentStateException>(() => storage.WriteStateAsync("Account", id, stale, token));
        Assert.Null(error.StoredEtag);
        Assert.Equal(retainedEtag, error.CurrentEtag);
        Assert.Equal(retainedEtag, stale.ETag);
        Assert.True(stale.RecordExists);
        Assert.Same(original, stale.State);
        Assert.Equal("stale", stale.State.Name);
        Assert.Equal(999, stale.State.Count);
        Assert.Equal(0, await collection.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: token));

        // A fresh missing read establishes an absent record, allowing a new insert.
        await storage.ReadStateAsync("Account", id, stale, token);
        Assert.False(stale.RecordExists);
        Assert.Null(stale.ETag);
        stale.State.Name = "recreated";
        await storage.WriteStateAsync("Account", id, stale, token);
        Assert.True(stale.RecordExists);
        Assert.False(string.IsNullOrWhiteSpace(stale.ETag));
        Assert.Equal("recreated", (await collection.Find(new BsonDocument("_id", id.ToString())).SingleAsync(token))["_doc"]["Name"].AsString);
    }

    private static Microsoft.Extensions.Hosting.IHost CreateHost(MongoDatabase database)
        => MongoTestHost.Create(silo =>
        {
            silo.Services.AddSingleton<IMongoClient>(database.Client);
            silo.AddMongoDBGrainStorage("store", (MongoDBGrainStorageOptions options) =>
            {
                options.DatabaseName = database.Name;
                options.CollectionPrefix = "Legacy_";
            });
        });
}
