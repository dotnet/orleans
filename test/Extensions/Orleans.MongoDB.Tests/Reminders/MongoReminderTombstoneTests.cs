using MongoDB.Bson;
using MongoDB.Driver;
using Orleans.Providers.MongoDB.Reminders.Store;
using Orleans.Runtime;
using Xunit;

namespace Orleans.MongoDB.Tests.Reminders;

[TestProvider("MongoDB"), TestSuite("BVT"), TestArea("Reminders")]
public sealed class MongoReminderTombstoneTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public async Task SeededDeletionFlags_ApplyToEveryReadAndReregistration(bool? isDeleted)
    {
        await using var database = await MongoDatabase.CreateAsync();
        var table = database.CreateReminderTable("service");
        var token = TestContext.Current.CancellationToken;
        await table.StartAsync(token);
        var grainId = GrainId.Create("account", "tombstone");
        var target = new ReminderEntry
        {
            GrainId = grainId,
            ReminderName = "target",
            StartAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Period = TimeSpan.FromMinutes(3)
        };
        var id = $"service_{grainId}_target";
        var document = MongoReminderDocument.Create(id, "service", target, "seeded-etag").ToBsonDocument();
        if (isDeleted.HasValue) document.Add("IsDeleted", isDeleted.Value);
        var collection = database.Database.GetCollection<BsonDocument>("OrleansReminderV2");
        await collection.InsertOneAsync(document, cancellationToken: token);
        var neighbor = new ReminderEntry
        {
            GrainId = grainId,
            ReminderName = "neighbor",
            StartAt = target.StartAt,
            Period = TimeSpan.FromMinutes(7)
        };
        var neighborTag = await table.UpsertRow(neighbor, token);
        var hash = grainId.GetUniformHashCode();
        Assert.InRange(hash, 1u, uint.MaxValue - 1);

        var point = await table.ReadRow(grainId, "target", token);
        if (isDeleted == true) Assert.Null(point);
        else
        {
            Assert.Equal("seeded-etag", point!.ETag);
            Assert.Equal(target.StartAt, point.StartAt);
            Assert.Equal(TimeSpan.FromMinutes(3), point.Period);
        }

        ReminderTableData[] reads =
        [
            await table.ReadRows(grainId, token),
            await table.ReadRows(0, 0, token),
            await table.ReadRows(hash - 1, hash, token),
            await table.ReadRows(hash + 1, hash, token)
        ];
        foreach (var read in reads)
        {
            Assert.Equal(isDeleted == true ? 1 : 2, read.Reminders.Count);
            Assert.Equal(neighborTag, Assert.Single(read.Reminders, entry => entry.ReminderName == "neighbor").ETag);
            if (isDeleted == true) Assert.DoesNotContain(read.Reminders, entry => entry.ReminderName == "target");
            else Assert.Equal("seeded-etag", Assert.Single(read.Reminders, entry => entry.ReminderName == "target").ETag);
        }
        Assert.Equal(document, await collection.Find(new BsonDocument("_id", id)).SingleAsync(token));

        target.StartAt = target.StartAt.AddDays(1);
        target.Period = TimeSpan.FromMinutes(9);
        var registeredTag = await table.UpsertRow(target, token);
        Assert.False(string.IsNullOrWhiteSpace(registeredTag));
        Assert.NotEqual("seeded-etag", registeredTag);
        var updated = await collection.Find(new BsonDocument("_id", id)).SingleAsync(token);
        Assert.False(updated.Contains("IsDeleted"));
        Assert.Equal(registeredTag, updated["Etag"].AsString);
        Assert.Equal(2, await collection.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: token));
        var registered = await table.ReadRow(grainId, "target", token);
        Assert.Equal(registeredTag, registered!.ETag);
        Assert.Equal(target.StartAt, registered.StartAt);
        Assert.Equal(TimeSpan.FromMinutes(9), registered.Period);
        Assert.Equal(2, (await table.ReadRows(grainId, token)).Reminders.Count);
        Assert.Equal(2, (await table.ReadRows(0, 0, token)).Reminders.Count);
        Assert.Equal(2, (await table.ReadRows(hash - 1, hash, token)).Reminders.Count);
        Assert.Equal(2, (await table.ReadRows(hash + 1, hash, token)).Reminders.Count);
        Assert.False(await table.RemoveRow(grainId, "target", "seeded-etag", token));
        Assert.True(await table.RemoveRow(grainId, "target", registeredTag!, token));
        Assert.Null(await table.ReadRow(grainId, "target", token));
        Assert.Equal(neighborTag, Assert.Single((await table.ReadRows(0, 0, token)).Reminders).ETag);
    }

}
