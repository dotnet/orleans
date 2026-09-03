using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Grpc.Core;
using Google.Cloud.Firestore;
using Orleans.Clustering.Firestore;
using Utils = Orleans.Clustering.Firestore.Utils;

namespace Orleans.Clustering.Firestore.Tests;

[TestSuite("Functional")]
[TestProvider("GoogleCloud")]
[TestCategory("Functional"), TestCategory("Firestore"), TestCategory("GoogleCloud")]
public class FirestoreDataManagerTests : IAsyncLifetime
{
    private const string TEST_PARTITION = "Test";
    private FirestoreDataManager _manager = default!;

    [Fact]
    public async Task CreateEntry()
    {
        var data = GetDummyEntity();
        var eTag = await this._manager.CreateEntity(data, TestContext.Current.CancellationToken);

        var data2 = data.Clone();
        data2.Age = 99;
        var exception = await Assert.ThrowsAsync<RpcException>(
            () => this._manager.CreateEntity(data2, TestContext.Current.CancellationToken));
        Assert.Equal(StatusCode.AlreadyExists, exception.StatusCode);

        var returned = await this._manager.ReadEntity<DummyFirestoreEntity>(
            data.Id,
            TestContext.Current.CancellationToken);
        Assert.NotNull(returned);
        Assert.Equal(data.Id, returned.Id);
        Assert.Equal(data.Name, returned.Name);
        Assert.Equal(data.Age, returned.Age);
        Assert.Equal(Utils.ParseTimestamp(eTag), returned.ETag);
    }

    [Fact]
    public async Task CreateEntryRejectsWhitespaceId()
    {
        var data = GetDummyEntity();
        data.Id = " ";

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => this._manager.CreateEntity(data, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpsertEntry()
    {
        var data = GetDummyEntity();
        var etag1 = await this._manager.UpsertEntity(data, TestContext.Current.CancellationToken);

        var data2 = data.Clone();
        data2.Age = 99;

        var eTag2 = await this._manager.UpsertEntity(data2, TestContext.Current.CancellationToken);

        var returned = await this._manager.ReadEntity<DummyFirestoreEntity>(
            data.Id,
            TestContext.Current.CancellationToken);
        Assert.NotNull(returned);
        Assert.Equal(data.Id, returned.Id);
        Assert.Equal(data2.Name, returned.Name);
        Assert.Equal(data2.Age, returned.Age);
        Assert.Equal(Utils.ParseTimestamp(eTag2), returned.ETag);
    }

    [Fact]
    public async Task UpdateEntryRequiresIdAndEtag()
    {
        var data = GetDummyEntity();
        data.Id = default!;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => this._manager.Update(data, TestContext.Current.CancellationToken));

        data.Id = Guid.NewGuid().ToString();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => this._manager.Update(data, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateMissingEntryFails()
    {
        var data = GetDummyEntity();
        data.ETag = Timestamp.FromDateTimeOffset(new DateTimeOffset(2021, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var found = await this._manager.ReadEntity<DummyFirestoreEntity>(
            data.Id,
            TestContext.Current.CancellationToken);
        Assert.Null(found);

        var exception = await Assert.ThrowsAsync<RpcException>(
            () => this._manager.Update(data, TestContext.Current.CancellationToken));
        Assert.Equal(StatusCode.FailedPrecondition, exception.StatusCode);
    }

    [Fact]
    public async Task UpdateWithStaleEtagFails()
    {
        var data = GetDummyEntity();
        var eTag = await this._manager.CreateEntity(data, TestContext.Current.CancellationToken);

        var data2 = data.Clone();
        data2.Age = 99;
        data2.ETag = Timestamp.FromDateTimeOffset(new DateTimeOffset(2021, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var exception = await Assert.ThrowsAsync<RpcException>(
            () => this._manager.Update(data2, TestContext.Current.CancellationToken));
        Assert.Equal(StatusCode.FailedPrecondition, exception.StatusCode);

        var returned = await this._manager.ReadEntity<DummyFirestoreEntity>(
            data.Id,
            TestContext.Current.CancellationToken);
        Assert.NotNull(returned);
        Assert.Equal(data.Age, returned.Age);
        Assert.Equal(Utils.ParseTimestamp(eTag), returned.ETag);
    }

    [Fact]
    public async Task UpdateEntry()
    {
        var data = GetDummyEntity();
        var eTag = await this._manager.CreateEntity(data, TestContext.Current.CancellationToken);

        var data2 = data.Clone();
        data2.Age = 99;
        data2.ETag = Utils.ParseTimestamp(eTag);

        var eTag2 = await this._manager.Update(data2, TestContext.Current.CancellationToken);

        var returned = await this._manager.ReadEntity<DummyFirestoreEntity>(
            data.Id,
            TestContext.Current.CancellationToken);
        Assert.NotNull(returned);
        Assert.Equal(data.Id, returned.Id);
        Assert.Equal(data2.Name, returned.Name);
        Assert.Equal(data2.Age, returned.Age);
        Assert.Equal(Utils.ParseTimestamp(eTag2), returned.ETag!.Value);
    }

    [Fact]
    public async Task DeleteEntry()
    {
        var data = GetDummyEntity();

        var result = await this._manager.DeleteEntity(
            data.Id,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result, "A missing entry should not be deleted.");

        await this._manager.CreateEntity(data, TestContext.Current.CancellationToken);

        var found = await this._manager.ReadEntity<DummyFirestoreEntity>(
            data.Id,
            TestContext.Current.CancellationToken);
        Assert.NotNull(found);

        result = await this._manager.DeleteEntity(
            data.Id,
            Utils.FormatTimestamp(Timestamp.FromDateTimeOffset(new DateTimeOffset(2021, 1, 1, 0, 0, 0, TimeSpan.Zero))),
            TestContext.Current.CancellationToken);
        Assert.False(result, "An entry with a stale ETag should not be deleted.");

        result = await this._manager.DeleteEntity(
            data.Id,
            Utils.FormatTimestamp(found.ETag!.Value),
            TestContext.Current.CancellationToken);
        Assert.True(result, "An entry with the current ETag should be deleted.");

        found = await this._manager.ReadEntity<DummyFirestoreEntity>(
            data.Id,
            TestContext.Current.CancellationToken);
        Assert.Null(found);

        result = await this._manager.DeleteEntity(
            data.Id,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result, "A previously deleted entry should not be deleted again.");
    }

    [Fact]
    public async Task ReadAllEntries()
    {
        var data = GetDummyEntity();
        var eTag = await this._manager.CreateEntity(data, TestContext.Current.CancellationToken);

        var data2 = GetDummyEntity();
        var eTag2 = await this._manager.CreateEntity(data2, TestContext.Current.CancellationToken);

        var data3 = GetDummyEntity();
        var eTag3 = await this._manager.CreateEntity(data3, TestContext.Current.CancellationToken);

        var all = await this._manager.ReadAllEntities<DummyFirestoreEntity>(
            TestContext.Current.CancellationToken);
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

        await this._manager.DeleteEntity(data.Id, eTag, TestContext.Current.CancellationToken);
        await this._manager.DeleteEntity(data2.Id, eTag2, TestContext.Current.CancellationToken);

        all = await this._manager.ReadAllEntities<DummyFirestoreEntity>(
            TestContext.Current.CancellationToken);

        Assert.NotNull(all);
        Assert.Single(all);

        found = all.FirstOrDefault(x => x.Id == data3.Id);
        Assert.NotNull(found);
        Assert.Equal(data3.Id, found.Id);
        Assert.Equal(data3.Name, found.Name);
        Assert.Equal(data3.Age, found.Age);
        Assert.Equal(Utils.ParseTimestamp(eTag3), found.ETag);

        await this._manager.DeleteEntity(data3.Id, eTag3, TestContext.Current.CancellationToken);

        all = await this._manager.ReadAllEntities<DummyFirestoreEntity>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(all);
        Assert.Empty(all);
    }

    [Fact]
    public async Task DeleteEntitiesRejectsOversizedBatch()
    {
        var tasks = Enumerable.Range(0, FirestoreDataManager.MaxBatchSize + 1).Select(x =>
        {
            var entity = GetDummyEntity();
            return this._manager.CreateEntity(entity, TestContext.Current.CancellationToken);
        });

        await Task.WhenAll(tasks);

        var entities = await this._manager.ReadAllEntities<DummyFirestoreEntity>(
            TestContext.Current.CancellationToken);
        Assert.Equal(FirestoreDataManager.MaxBatchSize + 1, entities.Length);

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => this._manager.DeleteEntities(entities, TestContext.Current.CancellationToken));
        Assert.Equal("entities", exception.ParamName);
    }

    [Fact]
    public async Task DeleteEntitiesRejectsStaleEtag()
    {
        await Task.WhenAll(Enumerable.Range(0, 2)
            .Select(_ => this._manager.CreateEntity(
                GetDummyEntity(),
                TestContext.Current.CancellationToken)));

        var entities = await this._manager.ReadAllEntities<DummyFirestoreEntity>(
            TestContext.Current.CancellationToken);
        entities[0].ETag = Timestamp.FromDateTimeOffset(new DateTimeOffset(2021, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var exception = await Assert.ThrowsAsync<RpcException>(
            () => this._manager.DeleteEntities(entities, TestContext.Current.CancellationToken));
        Assert.Equal(StatusCode.FailedPrecondition, exception.StatusCode);
        Assert.Equal(
            2,
            (await this._manager.ReadAllEntities<DummyFirestoreEntity>(
                TestContext.Current.CancellationToken)).Length);
    }

    [Fact]
    public async Task DeleteEntitiesRejectsInvalidEntity()
    {
        var data = GetDummyEntity();
        await this._manager.CreateEntity(data, TestContext.Current.CancellationToken);

        var entities = await this._manager.ReadAllEntities<DummyFirestoreEntity>(
            TestContext.Current.CancellationToken);
        entities[0].ETag = Timestamp.FromDateTimeOffset(DateTimeOffset.MinValue);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => this._manager.DeleteEntities(entities, TestContext.Current.CancellationToken));
        Assert.Single(await this._manager.ReadAllEntities<DummyFirestoreEntity>(
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteEntities()
    {
        await Task.WhenAll(Enumerable.Range(0, 3)
            .Select(_ => this._manager.CreateEntity(
                GetDummyEntity(),
                TestContext.Current.CancellationToken)));

        var entities = await this._manager.ReadAllEntities<DummyFirestoreEntity>(
            TestContext.Current.CancellationToken);
        Assert.Equal(3, entities.Length);
        await this._manager.DeleteEntities(entities, TestContext.Current.CancellationToken);

        entities = await this._manager.ReadAllEntities<DummyFirestoreEntity>(
            TestContext.Current.CancellationToken);
        Assert.Empty(entities);

        await this._manager.DeleteEntities(entities, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task QueryEntities()
    {
        var data = GetDummyEntity();
        data.Age = 21;
        await this._manager.CreateEntity(data, TestContext.Current.CancellationToken);
        data = (await this._manager.ReadEntity<DummyFirestoreEntity>(
            data.Id,
            TestContext.Current.CancellationToken))!;

        var data2 = GetDummyEntity();
        data2.Age = 60;
        await this._manager.CreateEntity(data2, TestContext.Current.CancellationToken);
        data2 = (await this._manager.ReadEntity<DummyFirestoreEntity>(
            data2.Id,
            TestContext.Current.CancellationToken))!;

        var data3 = GetDummyEntity();
        data3.Age = 10;
        await this._manager.CreateEntity(data3, TestContext.Current.CancellationToken);
        data3 = (await this._manager.ReadEntity<DummyFirestoreEntity>(
            data3.Id,
            TestContext.Current.CancellationToken))!;

        var entities = await this._manager.QueryEntities<DummyFirestoreEntity>(
            x => x.WhereGreaterThanOrEqualTo("Age", 18),
            TestContext.Current.CancellationToken);

        Assert.NotNull(entities);
        Assert.Equal(2, entities.Length);

        var found = entities.FirstOrDefault(x => x.Id == data.Id);
        Assert.NotNull(found);
        Assert.Equal(data.Id, found.Id);
        Assert.Equal(data.Name, found.Name);
        Assert.Equal(data.Age, found.Age);
        Assert.Equal(data.ETag, found.ETag);

        entities = await this._manager.QueryEntities<DummyFirestoreEntity>(
            x => x.WhereLessThan("Age", 18),
            TestContext.Current.CancellationToken);

        Assert.NotNull(entities);
        Assert.Single(entities);

        found = entities.FirstOrDefault(x => x.Id == data3.Id);
        Assert.NotNull(found);
        Assert.Equal(data3.Id, found.Id);
        Assert.Equal(data3.Name, found.Name);
        Assert.Equal(data3.Age, found.Age);
        Assert.Equal(data3.ETag, found.ETag);

        entities = await this._manager.QueryEntities<DummyFirestoreEntity>(
            x => x.WhereGreaterThan("Age", 60),
            TestContext.Current.CancellationToken);

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

    public async ValueTask InitializeAsync()
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

        await this._manager.Initialize(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

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
