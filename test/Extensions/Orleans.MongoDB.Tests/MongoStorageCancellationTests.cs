using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using Orleans.Hosting;
using Orleans.MongoDB.Tests.Clustering;
using Orleans.MongoDB.Tests.Persistence;
using Orleans.Persistence.MongoDB;
using Orleans.Providers.MongoDB.Reminders.Store;
using Orleans.Runtime;
using Orleans.Storage;
using Xunit;

namespace Orleans.MongoDB.Tests;

[TestProvider("MongoDB"), TestSuite("BVT"), TestArea("Storage")]
public sealed class MongoStorageCancellationTests
{
    [Fact]
    public async Task PrecancelledStorageAndReminderOperations_PreserveMetadataAndPerformNoIo()
    {
        var commands = new ConcurrentQueue<string>();
        await using var database = await MongoDatabase.CreateAsync(settings =>
            settings.ClusterConfigurator = cluster => cluster.Subscribe<CommandStartedEvent>(e => commands.Enqueue(e.CommandName)));
        using var host = MongoTestHost.Create(silo =>
        {
            silo.Services.AddSingleton(database.Client);
            silo.AddMongoDBGrainStorage("state", (MongoDBGrainStorageOptions options) => options.DatabaseName = database.Name);
        });
        var storage = host.Services.GetRequiredKeyedService<IGrainStorage>("state");
        var reminders = database.CreateReminderTable("service");
        await reminders.StartAsync(TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        var token = cancellation.Token;
        var id = GrainId.Create("account", "cancelled");
        var state = new GrainState<MongoTestState> { State = new MongoTestState { Count = 42 }, ETag = "unchanged", RecordExists = true };
        var entry = Reminder(id);
        entry.ETag = "unchanged";
        commands.Clear();
        Func<Task>[] operations =
        [
            () => storage.ReadStateAsync("state", id, state, token),
            () => storage.WriteStateAsync("state", id, state, token),
            () => storage.ClearStateAsync("state", id, state, token),
            () => reminders.StartAsync(token),
            () => reminders.ReadRow(id, "bill", token),
            () => reminders.ReadRows(id, token),
            () => reminders.ReadRows(0, 0, token),
            () => reminders.UpsertRow(entry, token),
            () => reminders.RemoveRow(id, "bill", "unchanged", token),
            () => reminders.TestOnlyClearTable(token)
        ];
        foreach (var operation in operations)
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(operation);
            Assert.Equal(token, error.CancellationToken);
        }

        Assert.Empty(commands);
        Assert.Equal("unchanged", state.ETag);
        Assert.Equal(42, state.State.Count);
        Assert.True(state.RecordExists);
        Assert.Equal("unchanged", entry.ETag);
    }

    [Theory]
    [InlineData("Read", "find")]
    [InlineData("Insert", "insert")]
    [InlineData("Write", "update")]
    [InlineData("Clear", "delete")]
    [InlineData("WriteConflict", "find")]
    [InlineData("ClearConflict", "find")]
    public async Task PersistenceCancellation_ReachesDriverIncludingConflictLookups(string operation, string command)
    {
        var barrier = new MongoMembershipCancellationTests.CommandBarrier(1, command);
        await using var database = await MongoDatabase.CreateAsync(settings =>
            settings.ClusterConfigurator = cluster => cluster.Subscribe<CommandStartedEvent>(barrier.OnCommand));
        using var host = MongoTestHost.Create(silo =>
        {
            silo.Services.AddSingleton(database.Client);
            silo.AddMongoDBGrainStorage("store", (MongoDBGrainStorageOptions options) => options.DatabaseName = database.Name);
        });
        var storage = host.Services.GetRequiredKeyedService<IGrainStorage>("store");
        var id = GrainId.Create("account", "in-flight");
        var state = new GrainState<MongoTestState> { State = new MongoTestState { Count = 42 } };
        if (operation != "Insert")
        {
            await storage.WriteStateAsync("Account", id, state, TestContext.Current.CancellationToken);
        }
        if (operation.EndsWith("Conflict", StringComparison.Ordinal)) state.ETag = "stale";
        var etag = state.ETag;
        var recordExists = state.RecordExists;
        await AssertCancellation(barrier, token => operation switch
        {
            "Read" => storage.ReadStateAsync("Account", id, state, token),
            "Insert" or "Write" or "WriteConflict" => storage.WriteStateAsync("Account", id, state, token),
            "Clear" or "ClearConflict" => storage.ClearStateAsync("Account", id, state, token),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        });
        Assert.Equal(etag, state.ETag);
        Assert.Equal(recordExists, state.RecordExists);
        Assert.Equal(42, state.State.Count);
    }

    [Theory]
    [InlineData("Point", "find", 1)]
    [InlineData("SeededPoint", "find", 1)]
    [InlineData("Grain", "find", 1)]
    [InlineData("Range", "find", 1)]
    [InlineData("Upsert", "update", 1)]
    [InlineData("Remove", "delete", 1)]
    [InlineData("Clear", "delete", 1)]
    [InlineData("Initialize", "createIndexes", 1)]
    [InlineData("InitializeCleanup", "dropIndexes", 1)]
    public async Task ReminderCancellation_ReachesDriverIncludingLazyInitialization(string operation, string command, int occurrence)
    {
        var barrier = new MongoMembershipCancellationTests.CommandBarrier(occurrence, command);
        await using var database = await MongoDatabase.CreateAsync(settings =>
            settings.ClusterConfigurator = cluster => cluster.Subscribe<CommandStartedEvent>(barrier.OnCommand));
        var table = database.CreateReminderTable("service");
        var token = TestContext.Current.CancellationToken;
        await table.StartAsync(token);
        var entry = Reminder(GrainId.Create("account", "in-flight"));
        if (operation == "SeededPoint")
        {
            await database.Database.GetCollection<MongoReminderDocument>("OrleansReminderV2")
                .InsertOneAsync(MongoReminderDocument.Create($"service_{entry.GrainId}_bill", "service", entry, "legacy"), cancellationToken: token);
        }
        else if (!operation.StartsWith("Initialize", StringComparison.Ordinal))
        {
            await table.UpsertRow(entry, token);
        }
        var etag = entry.ETag;
        await AssertCancellation(barrier, cancellationToken => operation switch
        {
            "Point" or "SeededPoint" => table.ReadRow(entry.GrainId, "bill", cancellationToken),
            "Grain" => table.ReadRows(entry.GrainId, cancellationToken),
            "Range" or "Initialize" or "InitializeCleanup" => table.ReadRows(0, 0, cancellationToken),
            "Upsert" => table.UpsertRow(entry, cancellationToken),
            "Remove" => table.RemoveRow(entry.GrainId, "bill", entry.ETag!, cancellationToken),
            "Clear" => table.TestOnlyClearTable(cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        });
        Assert.Equal(etag, entry.ETag);
    }

    private static async Task AssertCancellation(MongoMembershipCancellationTests.CommandBarrier barrier, Func<CancellationToken, Task> action)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        barrier.Armed = true;
        var pending = Task.Run(() => action(cancellation.Token), TestContext.Current.CancellationToken);
        try
        {
            var completed = await Task.WhenAny(barrier.Entered.Task, pending).WaitAsync(TestContext.Current.CancellationToken);
            if (completed == pending) await pending;
            Assert.False(pending.IsCompleted);
        }
        finally
        {
            cancellation.Cancel();
            barrier.Armed = false;
            barrier.Release();
            try { await pending; }
            catch (OperationCanceledException) { } // Observe the owned operation before fixture cleanup.
        }

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    private static ReminderEntry Reminder(GrainId id) => new()
    {
        GrainId = id,
        ReminderName = "bill",
        StartAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        Period = TimeSpan.FromMinutes(3)
    };
}
