using System.Collections.Concurrent;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using Orleans.MongoDB.Tests.Clustering;
using Orleans.Providers.MongoDB.Reminders.Store;
using Orleans.Runtime;
using Xunit;

namespace Orleans.MongoDB.Tests.Reminders;

[TestProvider("MongoDB"), TestSuite("BVT"), TestArea("Reminders")]
public sealed class MongoReminderIdentityTests
{
    [Fact]
    public async Task ConcurrentFirstRegistrations_CompleteWithoutRetriesAndKeepOneReminder()
    {
        const int registrations = 8;
        var commands = new ConcurrentQueue<BsonDocument>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        await using var database = await MongoDatabase.CreateAsync(settings =>
            settings.ClusterConfigurator = cluster => cluster.Subscribe<CommandStartedEvent>(command =>
            {
                if (command.CommandName != "update") return;
                commands.Enqueue(command.Command.DeepClone().AsBsonDocument);
                if (Interlocked.Increment(ref arrivals) == registrations) entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            }));
        var table = database.CreateReminderTable("service");
        var token = TestContext.Current.CancellationToken;
        await table.StartAsync(token);
        // Initialize indexes before arming all concurrent first writes.
        Assert.Empty((await table.ReadRows(0, 0, token)).Reminders);
        var id = GrainId.Create("account", "concurrent-first");
        var entries = Enumerable.Range(1, registrations).Select(index =>
        {
            var entry = Entry(id, "bill");
            entry.Period = TimeSpan.FromMinutes(index);
            return entry;
        }).ToArray();
        var writes = entries.Select(entry => Task.Run(() => table.UpsertRow(entry, token), token)).ToArray();
        var allWrites = Task.WhenAll(writes);
        try
        {
            var reached = await Task.WhenAny(entered.Task, allWrites).WaitAsync(token);
            if (reached == allWrites) await allWrites;
            Assert.Equal(registrations, commands.Count);
            Assert.All(commands, command =>
            {
                var update = Assert.Single(command["updates"].AsBsonArray).AsBsonDocument;
                Assert.Equal(new[] { "_id" }, update["q"].AsBsonDocument.Names);
                Assert.StartsWith("v2:", update["q"]["_id"].AsString);
                Assert.True(update["upsert"].AsBoolean);
            });
        }
        finally
        {
            release.TrySetResult();
            await allWrites;
        }

        var etags = await allWrites;
        Assert.All(etags, tag => Assert.False(string.IsNullOrWhiteSpace(tag)));
        Assert.Equal(registrations, etags.Distinct().Count());
        Assert.Equal(registrations, commands.Count);
        var documents = await database.Database.GetCollection<MongoReminderDocument>("OrleansReminderV2")
            .Find(FilterDefinition<MongoReminderDocument>.Empty).ToListAsync(token);
        var document = Assert.Single(documents);
        var winner = Assert.Single(entries, entry => entry.ETag == document.Etag);
        Assert.Equal(winner.Period, document.Period);
        var point = await table.ReadRow(id, "bill", token);
        Assert.Equal(winner.ETag, point!.ETag);
        Assert.Equal(winner.Period, point.Period);
        Assert.Equal(winner.ETag, Assert.Single((await table.ReadRows(0, 0, token)).Reminders).ETag);
        Assert.True(await table.RemoveRow(id, "bill", winner.ETag!, token));
        Assert.Null(await table.ReadRow(id, "bill", token));
        Assert.Empty((await table.ReadRows(0, 0, token)).Reminders);
    }

    [Fact]
    public async Task ConcurrentLegacyRemoveAndReregister_UsesOneEncodedRegistration()
    {
        var barrier = new MongoMembershipCancellationTests.CommandBarrier(1, "update");
        await using var database = await MongoDatabase.CreateAsync(settings =>
            settings.ClusterConfigurator = cluster => cluster.Subscribe<CommandStartedEvent>(barrier.OnCommand));
        var firstTable = database.CreateReminderTable("service");
        var secondTable = database.CreateReminderTable("service");
        var token = TestContext.Current.CancellationToken;
        await firstTable.StartAsync(token);
        await secondTable.StartAsync(token);
        var first = Entry(GrainId.Create("account", "legacy-race"), "bill");
        first.Period = TimeSpan.FromMinutes(9);
        var second = Entry(first.GrainId, first.ReminderName);
        second.Period = TimeSpan.FromMinutes(7);
        var legacyId = $"service_{first.GrainId}_{first.ReminderName}";
        var collection = database.Database.GetCollection<MongoReminderDocument>("OrleansReminderV2");
        await collection.InsertOneAsync(MongoReminderDocument.Create(legacyId, "service", Entry(first.GrainId, "bill"), "legacy"),
            cancellationToken: token);

        barrier.Armed = true;
        var pending = Task.Run(() => firstTable.UpsertRow(first, token), token);
        string? secondTag = null;
        try
        {
            var entered = await Task.WhenAny(barrier.Entered.Task, pending).WaitAsync(token);
            if (entered == pending) await pending;
            Assert.False(pending.IsCompleted);
            var command = Assert.Single(barrier.Commands);
            Assert.Equal(legacyId, command["updates"][0]["q"]["_id"].AsString);
            barrier.Armed = false;
            Assert.True(await secondTable.RemoveRow(first.GrainId, "bill", "legacy", token));
            secondTag = await secondTable.UpsertRow(second, token);
            Assert.Equal(secondTag, Assert.Single((await secondTable.ReadRows(0, 0, token)).Reminders).ETag);
            Assert.Equal(TimeSpan.FromMinutes(7), (await secondTable.ReadRow(first.GrainId, "bill", token))!.Period);
        }
        finally
        {
            barrier.Armed = false;
            barrier.Release();
            await pending;
        }

        var firstTag = await pending;
        Assert.NotEqual(secondTag, firstTag);
        var stored = Assert.Single(await collection.Find(FilterDefinition<MongoReminderDocument>.Empty).ToListAsync(token));
        Assert.NotEqual(legacyId, stored.Id);
        Assert.StartsWith("v2:", stored.Id);
        Assert.Equal(firstTag, stored.Etag);
        Assert.Equal(TimeSpan.FromMinutes(9), stored.Period);
        var row = Assert.Single((await firstTable.ReadRows(0, 0, token)).Reminders);
        Assert.Equal(firstTag, row.ETag);
        var point = await firstTable.ReadRow(first.GrainId, "bill", token);
        Assert.Equal(firstTag, point!.ETag);
        Assert.Equal(TimeSpan.FromMinutes(9), point.Period);
        Assert.False(barrier.Commands.First()["updates"][0].AsBsonDocument.GetValue("upsert", false).AsBoolean);
        Assert.False(await secondTable.RemoveRow(first.GrainId, "bill", secondTag!, token));
        Assert.True(await firstTable.RemoveRow(first.GrainId, "bill", firstTag!, token));
        Assert.Null(await firstTable.ReadRow(first.GrainId, "bill", token));
        Assert.Empty((await firstTable.ReadRows(0, 0, token)).Reminders);
        Assert.Equal(0, await collection.CountDocumentsAsync(FilterDefinition<MongoReminderDocument>.Empty, cancellationToken: token));
    }

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
