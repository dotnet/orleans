using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Grpc.Core;
using Google.Cloud.Firestore;
using Orleans.Tests.GoogleFirestore;

namespace Orleans.Tests.Google;

[TestCategory("Functional"), TestCategory("GoogleFirestore"), TestCategory("GoogleCloud")]
public class FirestoreDataManagerTests : IAsyncLifetime
{
    private const string TEST_PARTITION = "Test";
    private FirestoreDataManager _manager = default!;

    [SkippableFact]
    public async Task CreateEntry()
    {
        var data = GetDummyEntity();
        var eTag = await this._manager.CreateEntity(data);

        try
        {
            var data2 = data.Clone();
            data2.Age = 99;
            await this._manager.CreateEntity(data2);
            Assert.True(false, "Should have thrown RpcException.");
        }
        catch (RpcException exc)
        {
            Assert.Equal(StatusCode.AlreadyExists, exc.StatusCode);  // "Creating an already existing entry."
        }
        var returned = await this._manager.ReadEntity<DummyFirestoreEntity>(data.Id);
        Assert.NotNull(returned);
        Assert.Equal(data.Id, returned.Id);
        Assert.Equal(data.Name, returned.Name);
        Assert.Equal(data.Age, returned.Age);
        Assert.Equal(Utils.ParseTimestamp(eTag), returned.ETag);
    }

    [SkippableFact]
    public async Task UpsertEntry()
    {
        var data = GetDummyEntity();
        var etag1 = await this._manager.UpsertEntity(data);

        var data2 = data.Clone();
        data2.Age = 99;

        var eTag2 = await this._manager.UpsertEntity(data2);

        var returned = await this._manager.ReadEntity<DummyFirestoreEntity>(data.Id);
        Assert.NotNull(returned);
        Assert.Equal(data.Id, returned.Id);
        Assert.Equal(data2.Name, returned.Name);
        Assert.Equal(data2.Age, returned.Age);
        Assert.Equal(Utils.ParseTimestamp(eTag2), returned.ETag);
    }

    [SkippableFact]
    public async Task UpdateEntry()
    {
        var data = GetDummyEntity();
        data.Id = default!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => this._manager.Update(data));
        data.Id = Guid.NewGuid().ToString();
        await Assert.ThrowsAsync<InvalidOperationException>(() => this._manager.Update(data));
        data.ETag = Timestamp.FromDateTimeOffset(new DateTimeOffset(2021, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var found = await this._manager.ReadEntity<DummyFirestoreEntity>(data.Id);
        Assert.Null(found);

        try
        {
            await this._manager.Update(data);
            Assert.True(false, "Should have thrown RpcException.");
        }
        catch (RpcException exc)
        {
            Assert.Equal(StatusCode.FailedPrecondition, exc.StatusCode);  // "Updating a non-existing entry."
        }

        var eTag = await this._manager.CreateEntity(data);

        var data2 = data.Clone();
        data2.Age = 99;
        data2.ETag = Timestamp.FromDateTimeOffset(new DateTimeOffset(2021, 1, 1, 0, 0, 0, TimeSpan.Zero));

        string eTag2 = default!;

        try
        {
            eTag2 = await this._manager.Update(data2);
            Assert.True(false, "Should have thrown RpcException.");
        }
        catch (RpcException exc)
        {
            Assert.Equal(StatusCode.FailedPrecondition, exc.StatusCode);  // "Wrong eTag."
        }

        data2.ETag = Utils.ParseTimestamp(eTag);

        eTag2 = await this._manager.Update(data2);

        var returned = await this._manager.ReadEntity<DummyFirestoreEntity>(data.Id);
        Assert.NotNull(returned);
        Assert.Equal(data.Id, returned.Id);
        Assert.Equal(data2.Name, returned.Name);
        Assert.Equal(data2.Age, returned.Age);
        Assert.Equal(Utils.ParseTimestamp(eTag2), returned.ETag!.Value);
    }

    [SkippableFact]
    public async Task DeleteEntry()
    {
        var data = GetDummyEntity();

        var result = await this._manager.DeleteEntity(data.Id);
        Assert.False(result, "Should have thrown RpcException.");

        await this._manager.CreateEntity(data);

        var found = await this._manager.ReadEntity<DummyFirestoreEntity>(data.Id);
        Assert.NotNull(found);

        result = await this._manager.DeleteEntity(data.Id, Utils.FormatTimestamp(Timestamp.FromDateTimeOffset(new DateTimeOffset(2021, 1, 1, 0, 0, 0, TimeSpan.Zero))));
        Assert.False(result, "Should have not deleted.");

        result = await this._manager.DeleteEntity(data.Id, Utils.FormatTimestamp(found.ETag!.Value));
        Assert.True(result, "Should have deleted.");

        found = await this._manager.ReadEntity<DummyFirestoreEntity>(data.Id);
        Assert.Null(found);

        result = await this._manager.DeleteEntity(data.Id);
        Assert.False(result, "Should have not deleted as it wasn't found.");
    }

    [SkippableFact]
    public async Task ReadAllEntries()
    {
        var data = GetDummyEntity();
        var eTag = await this._manager.CreateEntity(data);

        var data2 = GetDummyEntity();
        var eTag2 = await this._manager.CreateEntity(data2);

        var data3 = GetDummyEntity();
        var eTag3 = await this._manager.CreateEntity(data3);

        var all = await this._manager.ReadAllEntities<DummyFirestoreEntity>();
        Assert.NotNull(all);
        Assert.Equal(3, all.Length);

        var found = all.FirstOrDefault(x => x.Id == data.Id);
        Assert.NotNull(found);
        Assert.Equal(data.Id, found.Id);
        Assert.Equal(data.Name, found.Name);
        Assert.Equal(data.Age, found.Age);
        Assert.Equal(Utils.ParseTimestamp(eTag), found.ETag);

        found = all.FirstOrDefault(x => x.Id == data2.Id);
        Assert.NotNull(found);
        Assert.Equal(data2.Id, found.Id);
        Assert.Equal(data2.Name, found.Name);
        Assert.Equal(data2.Age, found.Age);
        Assert.Equal(Utils.ParseTimestamp(eTag2), found.ETag);

        found = all.FirstOrDefault(x => x.Id == data3.Id);
        Assert.NotNull(found);
        Assert.Equal(data3.Id, found.Id);
        Assert.Equal(data3.Name, found.Name);
        Assert.Equal(data3.Age, found.Age);
        Assert.Equal(Utils.ParseTimestamp(eTag3), found.ETag);

        await this._manager.DeleteEntity(data.Id, eTag);
        await this._manager.DeleteEntity(data2.Id, eTag2);

        all = await this._manager.ReadAllEntities<DummyFirestoreEntity>();

        Assert.NotNull(all);
        Assert.Single(all);

        found = all.FirstOrDefault(x => x.Id == data3.Id);
        Assert.NotNull(found);
        Assert.Equal(data3.Id, found.Id);
        Assert.Equal(data3.Name, found.Name);
        Assert.Equal(data3.Age, found.Age);
        Assert.Equal(Utils.ParseTimestamp(eTag3), found.ETag);

        await this._manager.DeleteEntity(data3.Id, eTag3);

        all = await this._manager.ReadAllEntities<DummyFirestoreEntity>();
        Assert.NotNull(all);
        Assert.Empty(all);
    }

    [SkippableFact]
    public async Task DeleteEntities()
    {
        var tasks = Enumerable.Range(0, 501).Select(x =>
        {
            var entity = GetDummyEntity();
            return this._manager.CreateEntity(entity);
        });

        await Task.WhenAll(tasks);

        var entities = await this._manager.ReadAllEntities<DummyFirestoreEntity>();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => this._manager.DeleteEntities(entities)); // "Deleting more than 500 entries at once."

        await this._manager.DeleteEntity(entities[0].Id, Utils.FormatTimestamp(entities[0].ETag!.Value));

        entities = await this._manager.ReadAllEntities<DummyFirestoreEntity>();
        var correctEtag = entities[0].ETag;

        try
        {
            entities[0].ETag = Timestamp.FromDateTimeOffset(new DateTimeOffset(2021, 1, 1, 0, 0, 0, TimeSpan.Zero));
            await this._manager.DeleteEntities(entities);
            Assert.True(false, "Should have thrown RpcException.");
        }
        catch (RpcException exc)
        {
            Assert.Equal(StatusCode.FailedPrecondition, exc.StatusCode);  // "Wrong eTag."
        }


        entities[0].ETag = Timestamp.FromDateTimeOffset(DateTimeOffset.MinValue);
        await Assert.ThrowsAsync<InvalidOperationException>(() => this._manager.DeleteEntities(entities)); // "Deleting an entry with a wrong data."

        entities[0].ETag = correctEtag;

        await this._manager.DeleteEntities(entities);

        entities = await this._manager.ReadAllEntities<DummyFirestoreEntity>();

        Assert.Empty(entities);

        await this._manager.DeleteEntities(entities);
    }

    [SkippableFact]
    public async Task QueryEntities()
    {
        var data = GetDummyEntity();
        data.Age = 21;
        await this._manager.CreateEntity(data);
        data = (await this._manager.ReadEntity<DummyFirestoreEntity>(data.Id))!;

        var data2 = GetDummyEntity();
        data2.Age = 60;
        await this._manager.CreateEntity(data2);
        data2 = (await this._manager.ReadEntity<DummyFirestoreEntity>(data2.Id))!;

        var data3 = GetDummyEntity();
        data3.Age = 10;
        await this._manager.CreateEntity(data3);
        data3 = (await this._manager.ReadEntity<DummyFirestoreEntity>(data3.Id))!;

        var entities = await this._manager.QueryEntities<DummyFirestoreEntity>(x => x.WhereGreaterThanOrEqualTo("Age", 18));

        Assert.NotNull(entities);
        Assert.Equal(2, entities.Length);

        var found = entities.FirstOrDefault(x => x.Id == data.Id);
        Assert.NotNull(found);
        Assert.Equal(data.Id, found.Id);
        Assert.Equal(data.Name, found.Name);
        Assert.Equal(data.Age, found.Age);
        Assert.Equal(data.ETag, found.ETag);

        entities = await this._manager.QueryEntities<DummyFirestoreEntity>(x => x.WhereLessThan("Age", 18));

        Assert.NotNull(entities);
        Assert.Single(entities);

        found = entities.FirstOrDefault(x => x.Id == data3.Id);
        Assert.NotNull(found);
        Assert.Equal(data3.Id, found.Id);
        Assert.Equal(data3.Name, found.Name);
        Assert.Equal(data3.Age, found.Age);
        Assert.Equal(data3.ETag, found.ETag);

        entities = await this._manager.QueryEntities<DummyFirestoreEntity>(x => x.WhereGreaterThan("Age", 60));

        Assert.NotNull(entities);
        Assert.Empty(entities);
    }

    private static DummyFirestoreEntity GetDummyEntity()
    {
        return new DummyFirestoreEntity
        {
            Name = $"Test {Guid.NewGuid():N}",
            Age = Random.Shared.Next(1, 100)
        };
    }

    public async Task InitializeAsync()
    {
        var id = $"Orleans-Test-{Guid.NewGuid()}";
        var opt = new FirestoreOptions
        {
            ProjectId = "orleans-test",
            EmulatorHost = GoogleEmulatorHost.FirestoreEndpoint,
            RootCollectionName = id
        };

        this._manager = new FirestoreDataManager(
            "Test",
            TEST_PARTITION,
            opt,
            NullLoggerFactory.Instance.CreateLogger<FirestoreDataManager>());

        await this._manager.Initialize();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [FirestoreData]
    private class DummyFirestoreEntity : FirestoreEntity
    {
        [FirestoreProperty("Name")]
        public string Name { get; set; } = default!;

        [FirestoreProperty("Age")]
        public int Age { get; set; }

        public DummyFirestoreEntity()
        {
            this.Id = Guid.NewGuid().ToString();
        }

        public DummyFirestoreEntity Clone()
        {
            return new DummyFirestoreEntity
            {
                Id = this.Id,
                Name = this.Name,
                Age = this.Age,
                ETag = this.ETag
            };
        }

        public override IDictionary<string, object?> GetFields()
        {
            return new Dictionary<string, object?>
            {
                { "Name", this.Name },
                { "Age", this.Age }
            };
        }
    }
}