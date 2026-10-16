using MongoDB.Driver;
using Orleans.Providers.MongoDB.Reminders.Store;
using Orleans.Runtime;
using Xunit;

namespace Orleans.MongoDB.Tests.Reminders;

[TestProvider("MongoDB"), TestSuite("BVT"), TestArea("Reminders")]
public sealed class MongoReminderIdentityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CollidingServiceIds_PreserveReadRemoveAndClearIsolation(bool seedLegacy)
    {
        await using var database = await MongoDatabase.CreateAsync();
        var firstTable = database.CreateReminderTable("s");
        var secondTable = database.CreateReminderTable("s_a/b");
        var token = TestContext.Current.CancellationToken;
        await firstTable.StartAsync(token);
        await secondTable.StartAsync(token);
        var first = Entry(GrainId.Create("a", "b"), "c/d_e");
        var second = Entry(GrainId.Create("c", "d"), "e");
        var legacyId = $"s_{first.GrainId}_{first.ReminderName}";
        Assert.Equal(legacyId, $"s_a/b_{second.GrainId}_{second.ReminderName}");
        if (seedLegacy)
        {
            await database.Database.GetCollection<MongoReminderDocument>("OrleansReminderV2")
                .InsertOneAsync(MongoReminderDocument.Create(legacyId, "s", first, "legacy"), cancellationToken: token);
            first.ETag = "legacy";
            Assert.Null(await secondTable.ReadRow(second.GrainId, second.ReminderName, token));
            Assert.False(await secondTable.RemoveRow(second.GrainId, second.ReminderName, "legacy", token));
        }
        else first.ETag = await firstTable.UpsertRow(first, token);
        var secondTag = await secondTable.UpsertRow(second, token);
        Assert.Equal(first.ETag, (await firstTable.ReadRow(first.GrainId, first.ReminderName, token))!.ETag);
        Assert.Equal(secondTag, (await secondTable.ReadRow(second.GrainId, second.ReminderName, token))!.ETag);
        await firstTable.TestOnlyClearTable(token);
        Assert.Empty((await firstTable.ReadRows(0, 0, token)).Reminders);
        Assert.Equal(secondTag, Assert.Single((await secondTable.ReadRows(0, 0, token)).Reminders).ETag);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CollidingLegacyIds_KeepDistinctGrainAndReminderIdentities(bool seedLegacy)
    {
        await using var database = await MongoDatabase.CreateAsync();
        var table = database.CreateReminderTable("service");
        var token = TestContext.Current.CancellationToken;
        await table.StartAsync(token);
        var first = Entry(GrainId.Create("a", "b_c"), "d");
        var second = Entry(GrainId.Create("a", "b"), "c_d");
        var legacyId = $"service_{first.GrainId}_{first.ReminderName}";
        Assert.Equal(legacyId, $"service_{second.GrainId}_{second.ReminderName}");
        var collection = database.Database.GetCollection<MongoReminderDocument>("OrleansReminderV2");
        if (seedLegacy)
        {
            await collection.InsertOneAsync(MongoReminderDocument.Create(legacyId, "service", first, "legacy-etag"),
                cancellationToken: token);
            first.ETag = "legacy-etag";
            Assert.Null(await table.ReadRow(second.GrainId, second.ReminderName, token));
            Assert.False(await table.RemoveRow(second.GrainId, second.ReminderName, "legacy-etag", token));
        }
        else
        {
            first.ETag = await table.UpsertRow(first, token);
        }

        second.Period = TimeSpan.FromMinutes(7);
        var secondTag = await table.UpsertRow(second, token);
        Assert.Equal(first.ETag, (await table.ReadRow(first.GrainId, first.ReminderName, token))!.ETag);
        Assert.Equal(secondTag, (await table.ReadRow(second.GrainId, second.ReminderName, token))!.ETag);
        Assert.Equal(first.GrainId, Assert.Single((await table.ReadRows(first.GrainId, token)).Reminders).GrainId);
        Assert.Equal(second.GrainId, Assert.Single((await table.ReadRows(second.GrainId, token)).Reminders).GrainId);
        Assert.Equal(2, (await table.ReadRows(0, 0, token)).Reminders.Count);
        first.Period = TimeSpan.FromMinutes(9);
        var firstTag = await table.UpsertRow(first, token);
        Assert.Equal(TimeSpan.FromMinutes(9), (await table.ReadRow(first.GrainId, first.ReminderName, token))!.Period);
        Assert.Equal(TimeSpan.FromMinutes(7), (await table.ReadRow(second.GrainId, second.ReminderName, token))!.Period);
        var documents = await collection.Find(FilterDefinition<MongoReminderDocument>.Empty).ToListAsync(token);
        Assert.Equal(2, documents.Select(document => document.Id).Distinct().Count());
        if (seedLegacy)
        {
            Assert.Equal(legacyId, Assert.Single(documents, document => document.GrainId == first.GrainId.ToString()).Id);
        }
        else
        {
            Assert.All(documents, document => Assert.DoesNotContain("_", document.Id));
        }

        Assert.False(await table.RemoveRow(first.GrainId, first.ReminderName, secondTag!, token));
        Assert.True(await table.RemoveRow(first.GrainId, first.ReminderName, firstTag!, token));
        Assert.Null(await table.ReadRow(first.GrainId, first.ReminderName, token));
        Assert.Equal(secondTag, Assert.Single((await table.ReadRows(0, 0, token)).Reminders).ETag);
    }

    private static ReminderEntry Entry(GrainId grainId, string name) => new()
    {
        GrainId = grainId,
        ReminderName = name,
        StartAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        Period = TimeSpan.FromMinutes(3)
    };
}
