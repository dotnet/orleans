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
    public async Task ConcurrentFirstRegistrations_CompleteWithoutCallerRetriesAndKeepOneReminder()
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
                Assert.Equal($"service_{id}_bill", update["q"]["_id"].AsString);
                Assert.Equal("service", update["q"]["ServiceId"].AsString);
                Assert.Equal(id.ToString(), update["q"]["GrainId"].AsString);
                Assert.Equal("bill", update["q"]["ReminderName"].AsString);
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
        Assert.True(commands.Count >= registrations);
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
    public async Task ConcurrentRemoveAndReregister_UsesOneLegacyRegistration()
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
        Assert.Equal(legacyId, stored.Id);
        Assert.Equal(firstTag, stored.Etag);
        Assert.Equal(TimeSpan.FromMinutes(9), stored.Period);
        var row = Assert.Single((await firstTable.ReadRows(0, 0, token)).Reminders);
        Assert.Equal(firstTag, row.ETag);
        var point = await firstTable.ReadRow(first.GrainId, "bill", token);
        Assert.Equal(firstTag, point!.ETag);
        Assert.Equal(TimeSpan.FromMinutes(9), point.Period);
        Assert.True(barrier.Commands.First()["updates"][0].AsBsonDocument["upsert"].AsBoolean);
        Assert.False(await secondTable.RemoveRow(first.GrainId, "bill", secondTag!, token));
        Assert.True(await firstTable.RemoveRow(first.GrainId, "bill", firstTag!, token));
        Assert.Null(await firstTable.ReadRow(first.GrainId, "bill", token));
        Assert.Empty((await firstTable.ReadRows(0, 0, token)).Reminders);
        Assert.Equal(0, await collection.CountDocumentsAsync(FilterDefinition<MongoReminderDocument>.Empty, cancellationToken: token));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LegacyIdentityCollision_RejectsRegistrationAndPreservesStoredReminder(bool serviceCollision, bool seedLegacy)
    {
        await using var database = await MongoDatabase.CreateAsync();
        var firstService = serviceCollision ? "s" : "service";
        var secondService = serviceCollision ? "s_a/b" : "service";
        var firstTable = database.CreateReminderTable(firstService);
        var secondTable = database.CreateReminderTable(secondService);
        var token = TestContext.Current.CancellationToken;
        await firstTable.StartAsync(token);
        await secondTable.StartAsync(token);
        var first = serviceCollision ? Entry(GrainId.Create("a", "b"), "c/d_e") : Entry(GrainId.Create("a", "b_c"), "d");
        var second = serviceCollision ? Entry(GrainId.Create("c", "d"), "e") : Entry(GrainId.Create("a", "b"), "c_d");
        var legacyId = $"{firstService}_{first.GrainId}_{first.ReminderName}";
        Assert.Equal(legacyId, $"{secondService}_{second.GrainId}_{second.ReminderName}");
        var collection = database.Database.GetCollection<MongoReminderDocument>("OrleansReminderV2");
        if (seedLegacy)
        {
            await collection.InsertOneAsync(MongoReminderDocument.Create(legacyId, firstService, first, "legacy"), cancellationToken: token);
            first.ETag = "legacy";
        }
        else first.ETag = await firstTable.UpsertRow(first, token);
        second.ETag = "unchanged";
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => secondTable.UpsertRow(second, token));
        Assert.Contains("key collision", error.Message);
        Assert.Equal("unchanged", second.ETag);
        Assert.Null(await secondTable.ReadRow(second.GrainId, second.ReminderName, token));
        Assert.False(await secondTable.RemoveRow(second.GrainId, second.ReminderName, first.ETag!, token));
        Assert.Empty((await secondTable.ReadRows(second.GrainId, token)).Reminders);
        if (serviceCollision)
        {
            Assert.Empty((await secondTable.ReadRows(0, 0, token)).Reminders);
            await secondTable.TestOnlyClearTable(token);
        }
        Assert.Equal(first.ETag, (await firstTable.ReadRow(first.GrainId, first.ReminderName, token))!.ETag);
        Assert.Equal(first.ETag, Assert.Single((await firstTable.ReadRows(0, 0, token)).Reminders).ETag);
        var retained = Assert.Single(await collection.Find(FilterDefinition<MongoReminderDocument>.Empty).ToListAsync(token));
        Assert.Equal(legacyId, retained.Id);
        Assert.Equal(firstService, retained.ServiceId);
        Assert.Equal(first.GrainId.ToString(), retained.GrainId);
        Assert.Equal(first.ReminderName, retained.ReminderName);
        Assert.Equal(first.ETag, retained.Etag);
        Assert.Equal(TimeSpan.FromMinutes(3), retained.Period);
        first.Period = TimeSpan.FromMinutes(9);
        var updatedTag = await firstTable.UpsertRow(first, token);
        Assert.NotEqual(retained.Etag, updatedTag);
        Assert.Equal(TimeSpan.FromMinutes(9), (await firstTable.ReadRow(first.GrainId, first.ReminderName, token))!.Period);
        Assert.True(await firstTable.RemoveRow(first.GrainId, first.ReminderName, updatedTag!, token));
        Assert.Empty((await firstTable.ReadRows(0, 0, token)).Reminders);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpstreamAndUpdatedWriters_ShareReminderIdsAcrossRegistrationUpdateAndRemoval(bool updatedCreates)
    {
        await using var database = await MongoDatabase.CreateAsync();
        var table = database.CreateReminderTable("service");
        var token = TestContext.Current.CancellationToken;
        await table.StartAsync(token);
        var entry = Entry(GrainId.Create("account", "rolling-update"), "bill");
        var id = $"service_{entry.GrainId}_bill";
        var collection = database.Database.GetCollection<MongoReminderDocument>("OrleansReminderV2");
        if (updatedCreates) await table.UpsertRow(entry, token);
        else await UpstreamUpsert();
        Assert.Equal(entry.ETag, (await table.ReadRow(entry.GrainId, "bill", token))!.ETag);
        Assert.Equal(entry.ETag, (await collection.Find(row => row.Id == id).SingleAsync(token)).Etag);
        entry.Period = TimeSpan.FromMinutes(7);
        var upstreamTag = await UpstreamUpsert();
        Assert.Equal(upstreamTag, (await table.ReadRow(entry.GrainId, "bill", token))!.ETag);
        Assert.Equal(TimeSpan.FromMinutes(7), Assert.Single((await table.ReadRows(0, 0, token)).Reminders).Period);
        entry.Period = TimeSpan.FromMinutes(9);
        var updatedTag = await table.UpsertRow(entry, token);
        var upstreamRead = await collection.Find(row => row.Id == id).SingleAsync(token);
        Assert.Equal(updatedTag, upstreamRead.Etag);
        Assert.Equal(TimeSpan.FromMinutes(9), upstreamRead.Period);
        Assert.False(await table.RemoveRow(entry.GrainId, "bill", upstreamTag, token));
        // Original-provider point removal addresses only the legacy ID and ETag.
        var removed = await collection.DeleteOneAsync(row => row.Id == id && row.Etag == updatedTag, token);
        Assert.Equal(1, removed.DeletedCount);
        Assert.Null(await table.ReadRow(entry.GrainId, "bill", token));
        var recreated = await table.UpsertRow(entry, token);
        Assert.Equal(recreated, (await collection.Find(row => row.Id == id).SingleAsync(token)).Etag);
        Assert.True(await table.RemoveRow(entry.GrainId, "bill", recreated!, token));
        Assert.Empty(await collection.Find(FilterDefinition<MongoReminderDocument>.Empty).ToListAsync(token));

        async Task<string> UpstreamUpsert()
        {
            var document = MongoReminderDocument.Create(id, "service", entry, Guid.NewGuid().ToString());
            // Match the contributed provider's identity filter and mutable-field update.
            await collection.UpdateOneAsync(
                row => row.Id == id && row.ServiceId == "service" && row.GrainId == document.GrainId
                    && row.ReminderName == "bill" && row.GrainHash == document.GrainHash,
                Builders<MongoReminderDocument>.Update.Set(row => row.Etag, document.Etag)
                    .Set(row => row.Period, document.Period).Set(row => row.StartAt, document.StartAt),
                new UpdateOptions { IsUpsert = true }, token);
            entry.ETag = document.Etag;
            return document.Etag;
        }
    }

    private static ReminderEntry Entry(GrainId grainId, string name) => new()
    {
        GrainId = grainId,
        ReminderName = name,
        StartAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        Period = TimeSpan.FromMinutes(3)
    };
}
