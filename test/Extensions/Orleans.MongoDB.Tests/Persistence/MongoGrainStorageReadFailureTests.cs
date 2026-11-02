using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Orleans.Hosting;
using Orleans.Persistence.MongoDB;
using Orleans.Providers.MongoDB.StorageProviders.Serializers;
using Orleans.Runtime;
using Orleans.Storage;
using Xunit;

namespace Orleans.MongoDB.Tests.Persistence;

[TestProvider("MongoDB"), TestSuite("BVT"), TestArea("Persistence")]
public sealed class MongoGrainStorageReadFailureTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FailedDeserialization_PreservesStateAndMetadataAndPreventsUnreadOverwrite(bool legacy, bool recordExists)
    {
        await using var database = await MongoDatabase.CreateAsync();
        var serializer = new FailingReadSerializer();
        using var host = MongoTestHost.Create(silo =>
        {
            silo.Services.AddSingleton<IMongoClient>(database.Client);
            silo.AddMongoDBGrainStorage("store", (MongoDBGrainStorageOptions options) =>
            {
                options.DatabaseName = database.Name;
                options.CollectionPrefix = "Failure_";
                options.GrainStateSerializer = serializer;
            });
        });
        var storage = host.Services.GetRequiredKeyedService<IGrainStorage>("store");
        var id = GrainId.Create("account", "refresh-failure");
        var original = new MongoTestState { Name = "previous", Count = 7 };
        var state = new GrainState<MongoTestState> { State = original, ETag = "previous-etag", RecordExists = recordExists };
        var payload = new BsonDocument { { "Name", "unread" }, { "Count", 42 } };
        var document = legacy
            ? payload.DeepClone().AsBsonDocument
            : new BsonDocument { { "_etag", "stored-etag" }, { "_doc", payload } };
        document.InsertAt(0, new BsonElement("_id", id.ToString()));
        var collection = database.Database.GetCollection<BsonDocument>("Failure_Account");
        var token = TestContext.Current.CancellationToken;
        await collection.InsertOneAsync(document, cancellationToken: token);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => storage.ReadStateAsync("Account", id, state, token));
        Assert.Same(serializer.Error, exception);
        Assert.Same(original, state.State);
        Assert.Equal("previous", state.State.Name);
        Assert.Equal(7, state.State.Count);
        Assert.Equal("previous-etag", state.ETag);
        Assert.Equal(recordExists, state.RecordExists);
        var writeError = await Assert.ThrowsAsync<InconsistentStateException>(() => storage.WriteStateAsync("Account", id, state, token));
        Assert.Equal("previous-etag", writeError.CurrentEtag);
        Assert.Equal(legacy ? null : "stored-etag", writeError.StoredEtag);
        Assert.Equal(document, await collection.Find(new BsonDocument("_id", id.ToString())).SingleAsync(token));
        Assert.Same(original, state.State);
        Assert.Equal("previous-etag", state.ETag);
        Assert.Equal(recordExists, state.RecordExists);

        serializer.FailReads = false;
        await storage.ReadStateAsync("Account", id, state, token);
        Assert.NotSame(original, state.State);
        Assert.Equal("unread", state.State.Name);
        Assert.Equal(42, state.State.Count);
        Assert.Equal(legacy ? null : "stored-etag", state.ETag);
        Assert.True(state.RecordExists);
    }

    private sealed class FailingReadSerializer : IGrainStateSerializer
    {
        private readonly BsonGrainStateSerializer serializer = new();
        public InvalidDataException Error { get; } = new("Cannot deserialize stored state.");
        public bool FailReads { get; set; } = true;

        public BsonValue Serialize<T>(T state) => serializer.Serialize(state);

        public T Deserialize<T>(BsonValue value)
        {
            if (FailReads) throw Error;
            return serializer.Deserialize<T>(value);
        }
    }
}
