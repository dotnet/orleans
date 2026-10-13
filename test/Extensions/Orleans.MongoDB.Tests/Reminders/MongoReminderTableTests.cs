using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using Orleans.Configuration;
using Orleans.Reminders.MongoDB;
using Orleans.Providers.MongoDB.Reminders;
using Orleans.Reminders.TestKit;
using Orleans.Runtime;
using Xunit;

namespace Orleans.MongoDB.Tests.Reminders;

[TestProvider("MongoDB"), TestSuite("BVT"), TestArea("Reminders")]
public sealed class MongoReminderTableTests : IAsyncLifetime
{
    private MongoDatabase? database;
    private ReminderRunner? runner;
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private ReminderRunner Runner
    {
        get
        {
            MongoDatabase.RequireConnection();
            return runner!;
        }

    }

    private sealed class ReminderRunner(IReminderTable table)
        : ReminderTableTestRunner(table, "MongoDB", seed: 6143);

    public async ValueTask InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(MongoDatabase.ConnectionVariable))) return;
        database = await MongoDatabase.CreateAsync();
        try
        {
            var table = database.CreateReminderTable("mongo-reminder-tests");
            await table.StartAsync(Token);
            runner = new ReminderRunner(table);
        }
        catch
        {
            await database.DisposeAsync();
            database = null;
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (runner is not null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await runner.ReminderTable.StopAsync(cleanup.Token);
            }
        }
        finally
        {
            if (database is not null) await database.DisposeAsync();
        }
    }

    [Fact] public Task ReminderTable_StartAsync_IsIdempotent() => Runner.RunReminderTable_StartAsync_IsIdempotent(Token);
    [Fact] public Task ReminderTable_StopAsync_ThenRestart_ResumesService() => Runner.RunReminderTable_StopAsync_ThenRestart_ResumesService(Token);
    [Fact] public Task ReminderTable_UpsertRow_ReturnsNewNonEmptyETag() => Runner.RunReminderTable_UpsertRow_ReturnsNewNonEmptyETag(Token);
    [Fact] public Task ReminderTable_UpsertRow_PersistsScheduleForPointRead() => Runner.RunReminderTable_UpsertRow_PersistsScheduleForPointRead(Token);
    [Fact]
    public async Task ReminderTable_ReadRow_MissingReminder_ReturnsNull()
    {
        // Establish the positive read path too: an implementation returning null unconditionally must fail.
        await Runner.RunReminderTable_UpsertRow_PersistsScheduleForPointRead(Token);
        await Runner.RunReminderTable_ReadRow_MissingReminder_ReturnsNull(Token);
    }
    [Fact] public Task ReminderTable_ReadRows_ForGrain_ReturnsOnlyThatGrainsReminders() => Runner.RunReminderTable_ReadRows_ForGrain_ReturnsOnlyThatGrainsReminders(Token);
    [Fact]
    public async Task ReminderTable_ReadRows_ForUnknownGrain_ReturnsEmpty()
    {
        await Runner.RunReminderTable_ReadRows_ForGrain_ReturnsOnlyThatGrainsReminders(Token);
        await Runner.RunReminderTable_ReadRows_ForUnknownGrain_ReturnsEmpty(Token);
    }
    [Fact] public Task ReminderTable_Identity_IsGrainIdAndReminderName() => Runner.RunReminderTable_Identity_IsGrainIdAndReminderName(Token);
    [Fact] public Task ReminderTable_Identity_WithSpecialCharacters_RoundTrips() => Runner.RunReminderTable_Identity_WithSpecialCharacters_RoundTrips(Token);
    [Fact] public Task ReminderTable_UpsertRow_ReplacesETagOnEachWrite() => Runner.RunReminderTable_UpsertRow_ReplacesETagOnEachWrite(Token);
    [Fact] public Task ReminderTable_RemoveRow_WithCurrentETag_RemovesRow() => Runner.RunReminderTable_RemoveRow_WithCurrentETag_RemovesRow(Token);
    [Fact] public Task ReminderTable_RemoveRow_WithStaleETag_FailsAndRetainsRow() => Runner.RunReminderTable_RemoveRow_WithStaleETag_FailsAndRetainsRow(Token);
    [Fact] public Task ReminderTable_RemoveRow_WithUnknownReminderName_ReturnsFalse() => Runner.RunReminderTable_RemoveRow_WithUnknownReminderName_ReturnsFalse(Token);
    [Fact] public Task ReminderTable_RemoveRow_Repeated_ReturnsFalseAfterFirstSuccess() => Runner.RunReminderTable_RemoveRow_Repeated_ReturnsFalseAfterFirstSuccess(Token);
    [Fact] public Task ReminderTable_UpsertRow_UpdatesStartAtAndPeriod() => Runner.RunReminderTable_UpsertRow_UpdatesStartAtAndPeriod(Token);
    [Fact] public Task ReminderTable_UpsertRow_MovesReminderBetweenLoadingWindows() => Runner.RunReminderTable_UpsertRow_MovesReminderBetweenLoadingWindows(Token);
    [Fact] public Task ReminderTable_ReadRows_FullRange_ReturnsAllReminders() => Runner.RunReminderTable_ReadRows_FullRange_ReturnsAllReminders(Token);
    [Fact] public Task ReminderTable_ReadRows_UnsignedBoundary_UsesUInt32Ordering() => Runner.RunReminderTable_ReadRows_UnsignedBoundary_UsesUInt32Ordering(Token);
    [Theory, InlineData(1), InlineData(32), InlineData(128)]
    public Task ReminderTable_ReadRows_FullRange_ReturnsExactRequestedCardinality(int count)
        => Runner.RunReminderTable_ReadRows_FullRange_ReturnsExactRequestedCardinality(count, Token);
    [Fact] public Task ReminderTable_ReadRows_Range_ExcludesBeginAndIncludesEnd() => Runner.RunReminderTable_ReadRows_Range_ExcludesBeginAndIncludesEnd(Token);
    [Fact] public Task ReminderTable_ReadRows_WrapAroundRange_ReturnsWrappedSegment() => Runner.RunReminderTable_ReadRows_WrapAroundRange_ReturnsWrappedSegment(Token);
    [Fact] public Task ReminderTable_ReadRows_OutsideRange_DoesNotDeleteReminder() => Runner.RunReminderTable_ReadRows_OutsideRange_DoesNotDeleteReminder(Token);
    [Fact] public Task ReminderTable_ReadRows_AfterRemoval_OmitsRemovedReminder() => Runner.RunReminderTable_ReadRows_AfterRemoval_OmitsRemovedReminder(Token);
    [Fact] public Task ReminderTable_ReadRow_AfterRemoval_ReturnsNull() => Runner.RunReminderTable_ReadRow_AfterRemoval_ReturnsNull(Token);
    [Fact] public Task ReminderTable_ConcurrentUpserts_ProduceDistinctETags() => Runner.RunReminderTable_ConcurrentUpserts_ProduceDistinctETags(Token);
    [Fact] public Task ReminderTable_ParallelUpserts_AcrossGrains_RemainIsolated() => Runner.RunReminderTable_ParallelUpserts_AcrossGrains_RemainIsolated(Token);
    [Fact] public Task ReminderTable_TestOnlyClearTable_RemovesAllReminders() => Runner.RunReminderTable_TestOnlyClearTable_RemovesAllReminders(Token);

    [Fact]
    public async Task ServiceIds_WithOverlappingIdentity_AreIsolatedDuringReadRemoveAndClear()
    {
        var first = Runner.ReminderTable;
        var second = database!.CreateReminderTable("other-service");
        await second.StartAsync(Token);
        try
        {
            var id = GrainId.Create("account", "overlapping");
            var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var firstTag = await first.UpsertRow(new ReminderEntry
            {
                GrainId = id,
                ReminderName = "bill",
                StartAt = start,
                Period = TimeSpan.FromMinutes(3)
            }, Token);
            var secondTag = await second.UpsertRow(new ReminderEntry
            {
                GrainId = id,
                ReminderName = "bill",
                StartAt = start.AddDays(1),
                Period = TimeSpan.FromMinutes(7)
            }, Token);
            Assert.False(string.IsNullOrWhiteSpace(firstTag));
            Assert.False(string.IsNullOrWhiteSpace(secondTag));
            Assert.NotEqual(firstTag, secondTag);
            Assert.False(await first.RemoveRow(id, "bill", secondTag!, Token));
            Assert.Equal(firstTag, (await first.ReadRow(id, "bill", Token))!.ETag);
            await first.TestOnlyClearTable(Token);
            Assert.Null(await first.ReadRow(id, "bill", Token));
            Assert.Empty((await first.ReadRows(0, 0, Token)).Reminders);
            var remaining = Assert.Single((await second.ReadRows(0, 0, Token)).Reminders);
            Assert.Equal(id, remaining.GrainId);
            Assert.Equal("bill", remaining.ReminderName);
            Assert.Equal(secondTag, remaining.ETag);
            Assert.Equal(start.AddDays(1), remaining.StartAt);
            Assert.Equal(TimeSpan.FromMinutes(7), remaining.Period);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await second.StopAsync(cleanup.Token);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(null)]
    public async Task CreateIndexes_ControlsAllIndexCommandsWithoutPreventingReminderCrud(bool? createIndexes)
    {
        var commands = new ConcurrentQueue<string>();
        await using var owned = await MongoDatabase.CreateAsync(settings =>
            settings.ClusterConfigurator = cluster => cluster.Subscribe<CommandStartedEvent>(e =>
            {
                if (e.CommandName is "createIndexes" or "dropIndexes") commands.Enqueue(e.CommandName);
            }));
        var native = owned.Database.GetCollection<BsonDocument>("OrleansReminderV2");
        await native.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            new BsonDocument { { "IsDeleted", 1 }, { "ServiceId", 1 }, { "GrainHash", 1 } },
            new CreateIndexOptions { Name = "ByHash" }), cancellationToken: Token);
        await native.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            new BsonDocument { { "ServiceId", 1 }, { "GrainId", 1 }, { "ReminderName", 1 } },
            new CreateIndexOptions { Name = "ByName" }), cancellationToken: Token);
        commands.Clear();
        var options = new MongoDBRemindersOptions { DatabaseName = owned.Name };
#pragma warning disable CS0618 // Opt-out must bypass optional as well as mandatory legacy index removal.
        options.PurgeLegacyIndexes = true;
#pragma warning restore CS0618
        if (createIndexes is { } value) options.CreateIndexes = value;
        else Assert.True(options.CreateIndexes);
        IReminderTable table = new MongoReminderTable(owned.Client, NullLogger<MongoReminderTable>.Instance,
            Options.Create(options), Options.Create(new ClusterOptions { ServiceId = "index-policy", ClusterId = "tests" }));
        await table.StartAsync(Token);
        try
        {
            var entry = new ReminderEntry
            {
                GrainId = GrainId.Create("account", "index-policy"), ReminderName = "bill",
                StartAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), Period = TimeSpan.FromMinutes(3)
            };
            var tag = await table.UpsertRow(entry, Token);
            Assert.False(string.IsNullOrWhiteSpace(tag));
            var read = await table.ReadRow(entry.GrainId, entry.ReminderName, Token);
            Assert.NotNull(read);
            Assert.Equal(tag, read.ETag);
            Assert.Equal(entry.StartAt, read.StartAt);
            Assert.Equal(TimeSpan.FromMinutes(3), read.Period);
            Assert.True(await table.RemoveRow(entry.GrainId, entry.ReminderName, tag!, Token));
            Assert.Null(await table.ReadRow(entry.GrainId, entry.ReminderName, Token));
            using var cursor = await native.Indexes.ListAsync(Token);
            var indexes = await cursor.ToListAsync(Token);
            if (options.CreateIndexes)
            {
                Assert.Contains("createIndexes", commands);
                Assert.Contains("dropIndexes", commands);
                var index = Assert.Single(indexes, index => index["name"] == "ByGrainHash");
                Assert.Equal(new BsonDocument { { "ServiceId", 1 }, { "GrainHash", 1 } }, index["key"].AsBsonDocument);
                Assert.DoesNotContain(indexes, index => index["name"] == "ByHash");
                Assert.DoesNotContain(indexes, index => index["name"] == "ByName");
            }
            else
            {
                Assert.Empty(commands);
                var legacy = Assert.Single(indexes, index => index["name"] == "ByHash");
                Assert.Equal(new BsonDocument { { "IsDeleted", 1 }, { "ServiceId", 1 }, { "GrainHash", 1 } }, legacy["key"].AsBsonDocument);
                Assert.DoesNotContain(indexes, index => index["name"] == "ByGrainHash");
                var byName = Assert.Single(indexes, index => index["name"] == "ByName");
                Assert.Equal(new BsonDocument { { "ServiceId", 1 }, { "GrainId", 1 }, { "ReminderName", 1 } }, byName["key"].AsBsonDocument);
            }
        }
        finally { await table.StopAsync(Token); }
    }

    [Fact]
    public async Task InvalidReminderNames_RejectWithoutCreatingDocumentsOrChangingExistingReminder()
    {
        var table = Runner.ReminderTable;
        var id = GrainId.Create("account", "invalid-names");
        var entry = new ReminderEntry
        {
            GrainId = id, ReminderName = "valid", StartAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Period = TimeSpan.FromMinutes(5)
        };
        var tag = await table.UpsertRow(entry, Token);
        foreach (var name in new string?[] { null, "" })
        {
            var invalid = new ReminderEntry { GrainId = id, ReminderName = name!, ETag = "unchanged" };
            await Assert.ThrowsAnyAsync<ArgumentException>(() => table.UpsertRow(invalid, Token));
            await Assert.ThrowsAnyAsync<ArgumentException>(() => table.ReadRow(id, name!, Token));
            await Assert.ThrowsAnyAsync<ArgumentException>(() => table.RemoveRow(id, name!, tag!, Token));
            Assert.Equal("unchanged", invalid.ETag);
        }
        var remaining = Assert.Single((await table.ReadRows(id, Token)).Reminders);
        Assert.Equal("valid", remaining.ReminderName);
        Assert.Equal(tag, remaining.ETag);
        Assert.Equal(TimeSpan.FromMinutes(5), remaining.Period);
        Assert.Equal(1, await database!.Database.GetCollection<BsonDocument>("OrleansReminderV2")
            .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Token));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MalformedStoredReminder_ReadsSurfaceErrorAndPreserveDocument(bool missingGrainId)
    {
        var table = Runner.ReminderTable;
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("account", "malformed"), ReminderName = "bill",
            StartAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), Period = TimeSpan.FromMinutes(3)
        };
        var tag = await table.UpsertRow(entry, Token);
        var collection = database!.Database.GetCollection<BsonDocument>("OrleansReminderV2");
        var document = await collection.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync(Token);
        var identity = new BsonDocument("_id", document["_id"]);
        var malformed = missingGrainId
            ? new BsonDocument("$unset", new BsonDocument("GrainId", 1))
            : new BsonDocument("$set", new BsonDocument("GrainId", "not-a-grain-id"));
        await collection.UpdateOneAsync(identity, malformed, cancellationToken: Token);
        async Task Check(Func<Task> read)
        {
            if (missingGrainId)
            {
                var error = await Assert.ThrowsAsync<FormatException>(read);
                Assert.Contains("GrainId", error.Message);
            }
            else
            {
                var error = await Assert.ThrowsAsync<ArgumentException>(read);
                Assert.Contains("not-a-grain-id", error.Message);
            }
        }
        await Check(() => table.ReadRow(entry.GrainId, entry.ReminderName, Token));
        await Check(() => table.ReadRows(0, 0, Token));
        var retained = await collection.Find(identity).SingleAsync(Token);
        if (missingGrainId) Assert.False(retained.Contains("GrainId"));
        else Assert.Equal("not-a-grain-id", retained["GrainId"].AsString);
        Assert.Equal(tag, retained["Etag"].AsString);
        Assert.Equal("bill", retained["ReminderName"].AsString);
    }
}
