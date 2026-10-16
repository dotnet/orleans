using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using NSubstitute;
using Orleans.Persistence.MongoDB;
using Orleans.Providers.MongoDB.StorageProviders;
using Orleans.Providers.MongoDB.StorageProviders.Serializers;
using Orleans.Runtime;
using Orleans.Serialization.Serializers;
using Orleans.Storage;
using Xunit;

namespace Orleans.MongoDB.Tests.Persistence;

[TestProvider("MongoDB"), TestSuite("BVT"), TestArea("Persistence")]
public sealed class MongoGrainStoragePrimaryConflictTests
{
    [Theory]
    [InlineData("Clear", true)]
    [InlineData("Clear", false)]
    [InlineData("Write", true)]
    [InlineData("Write", false)]
    public async Task LaggingSecondary_ConflictCheckUsesPrimaryAndPreservesCallerOnRejection(string operation, bool storedOnPrimary)
    {
        using var host = MongoTestHost.Create();
        var client = Substitute.For<IMongoClient>();
        var database = Substitute.For<IMongoDatabase>();
        var secondary = Substitute.For<IMongoCollection<BsonDocument>>();
        var primary = Substitute.For<IMongoCollection<BsonDocument>>();
        var id = GrainId.Create("account", "replication-lag");
        var stored = new BsonDocument { { "_id", id.ToString() }, { "_etag", "primary-etag" } };
        client.GetDatabase("database", Arg.Any<MongoDatabaseSettings>()).Returns(database);
        database.GetCollection<BsonDocument>("GrainsAccount", Arg.Any<MongoCollectionSettings>()).Returns(secondary);
        foreach (var collection in new[] { secondary, primary })
        {
            collection.DocumentSerializer.Returns(BsonDocumentSerializer.Instance);
            collection.Settings.Returns(new MongoCollectionSettings
            {
                ReadPreference = collection == primary ? ReadPreference.Primary : ReadPreference.Secondary
            });
        }
        secondary.WithReadPreference(ReadPreference.Primary).Returns(primary);
        secondary.DeleteOneAsync(Arg.Any<FilterDefinition<BsonDocument>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<DeleteResult>(new DeleteResult.Acknowledged(0)));
        secondary.UpdateOneAsync(Arg.Any<FilterDefinition<BsonDocument>>(), Arg.Any<UpdateDefinition<BsonDocument>>(),
                Arg.Any<UpdateOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UpdateResult>(new UpdateResult.Acknowledged(0, 0, null)));
        secondary.FindAsync(Arg.Any<FilterDefinition<BsonDocument>>(), Arg.Any<FindOptions<BsonDocument, BsonDocument>>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(Cursor([])));
        primary.FindAsync(Arg.Any<FilterDefinition<BsonDocument>>(), Arg.Any<FindOptions<BsonDocument, BsonDocument>>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(Cursor(storedOnPrimary ? [stored] : [])));
        var storage = new MongoGrainStorage(client, NullLogger<MongoGrainStorage>.Instance,
            new MongoDBGrainStorageOptions { DatabaseName = "database", GrainStateSerializer = new BsonGrainStateSerializer() },
            host.Services.GetRequiredService<IActivatorProvider>());
        var original = new MongoTestState { Name = "previous", Count = 7 };
        var state = new GrainState<MongoTestState> { State = original, ETag = "stale-etag", RecordExists = true };
        var token = TestContext.Current.CancellationToken;

        // Model replication lag: the secondary has no document, even when the primary has one.
        Assert.Null(await secondary.Find(new BsonDocument("_id", id.ToString())).FirstOrDefaultAsync(token));
        secondary.ClearReceivedCalls();
        Func<Task> action = operation == "Clear"
            ? () => storage.ClearStateAsync("Account", id, state, token)
            : () => storage.WriteStateAsync("Account", id, state, token);
        if (storedOnPrimary || operation == "Write")
        {
            var error = await Assert.ThrowsAsync<InconsistentStateException>(action);
            Assert.Equal(storedOnPrimary ? "primary-etag" : null, error.StoredEtag);
            Assert.Equal("stale-etag", error.CurrentEtag);
            Assert.Same(original, state.State);
            Assert.Equal("previous", state.State.Name);
            Assert.Equal(7, state.State.Count);
            Assert.Equal("stale-etag", state.ETag);
            Assert.True(state.RecordExists);
        }
        else
        {
            await action();
            Assert.NotSame(original, state.State);
            Assert.Null(state.ETag);
            Assert.False(state.RecordExists);
        }
        secondary.Received(1).WithReadPreference(ReadPreference.Primary);
        await primary.Received(1).FindAsync(Arg.Any<FilterDefinition<BsonDocument>>(),
            Arg.Any<FindOptions<BsonDocument, BsonDocument>>(), token);
        await secondary.DidNotReceive().FindAsync(Arg.Any<FilterDefinition<BsonDocument>>(),
            Arg.Any<FindOptions<BsonDocument, BsonDocument>>(), Arg.Any<CancellationToken>());
        if (operation == "Clear")
        {
            await secondary.Received(1).DeleteOneAsync(Arg.Any<FilterDefinition<BsonDocument>>(), token);
        }
        else
        {
            await secondary.Received(1).UpdateOneAsync(Arg.Any<FilterDefinition<BsonDocument>>(),
                Arg.Any<UpdateDefinition<BsonDocument>>(), Arg.Is<UpdateOptions>(options => !options.IsUpsert), token);
        }
    }

    private static IAsyncCursor<BsonDocument> Cursor(BsonDocument[] documents)
    {
        var cursor = Substitute.For<IAsyncCursor<BsonDocument>>();
        cursor.Current.Returns(documents);
        cursor.MoveNextAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(true), Task.FromResult(false));
        return cursor;
    }
}
