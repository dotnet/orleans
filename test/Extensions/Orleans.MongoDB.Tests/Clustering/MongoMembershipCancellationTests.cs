using System.Collections.Concurrent;
using System.Net;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using Orleans.Providers.MongoDB.Configuration;
using Orleans.Runtime;
using Xunit;

namespace Orleans.MongoDB.Tests.Clustering;

[TestProvider("MongoDB"), TestSuite("BVT"), TestArea("Membership")]
public sealed class MongoMembershipCancellationTests
{
    public static TheoryData<MongoDBMembershipStrategy, string> Operations
    {
        get
        {
            var result = new TheoryData<MongoDBMembershipStrategy, string>();
            foreach (var strategy in Enum.GetValues<MongoDBMembershipStrategy>())
                foreach (var operation in new[] { "ReadAll", "ReadRow", "Insert", "Update", "Heartbeat", "Cleanup", "Delete" })
                    result.Add(strategy, operation);
            return result;
        }
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task InFlightNativeOperation_ForwardsCancellationToDriverAndTransaction(
        MongoDBMembershipStrategy strategy, string operation)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var token = cancellation.Token;
        var transaction = strategy == MongoDBMembershipStrategy.Multiple
            && operation is "ReadAll" or "ReadRow" or "Insert" or "Update" or "Delete";
        var blockAt = transaction || (strategy == MongoDBMembershipStrategy.SingleDocument && operation == "Cleanup") ? 2 : 1;
        var barrier = new CommandBarrier(blockAt);
        await using var database = await MongoDatabase.CreateAsync(settings =>
            settings.ClusterConfigurator = cluster => cluster.Subscribe<CommandStartedEvent>(barrier.OnCommand));
        var table = database.CreateMembershipTable("blocked", strategy);
        await table.InitializeMembershipTableAsync(true, token);
        var entry = new MembershipEntry
        {
            SiloAddress = SiloAddress.New(IPAddress.Loopback, 11111, 1), Status = SiloStatus.Dead,
            HostName = "blocked", SiloName = "blocked", ProxyPort = 30001,
            StartTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            IAmAliveTime = new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc)
        };
        Assert.True(await table.InsertRowAsync(entry, (await table.ReadAllAsync(token)).Version.Next(), token));
        var before = await table.ReadAllAsync(token);
        var row = Assert.Single(before.Members);
        var update = new MembershipEntry
        {
            SiloAddress = entry.SiloAddress, HostName = "updated", SiloName = "updated", Status = SiloStatus.Active,
            StartTime = entry.StartTime, IAmAliveTime = entry.IAmAliveTime.AddDays(1), ProxyPort = 40001
        };
        var inserted = new MembershipEntry
        {
            SiloAddress = SiloAddress.New(IPAddress.Loopback, 11112, 2), HostName = "inserted", SiloName = "inserted",
            Status = SiloStatus.Active, StartTime = entry.StartTime, IAmAliveTime = entry.IAmAliveTime, ProxyPort = 30002
        };
        barrier.Armed = true;
        var pending = Task.Run(() => operation switch
        {
            "ReadAll" => table.ReadAllAsync(token),
#pragma warning disable CS0618 // The obsolete native row-read method still promises token propagation.
            "ReadRow" => table.ReadRowAsync(entry.SiloAddress, token),
#pragma warning restore CS0618
            "Insert" => table.InsertRowAsync(inserted, before.Version.Next(), token),
            "Update" => table.UpdateRowAsync(update, row.Item2, before.Version.Next(), token),
            "Heartbeat" => table.UpdateIAmAliveAsync(update, token),
            "Cleanup" => table.CleanupDefunctSiloEntriesAsync(new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero), token),
            "Delete" => table.DeleteMembershipTableEntriesAsync("blocked", token),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        }, TestContext.Current.CancellationToken);
        try
        {
            var reached = await Task.WhenAny(barrier.Entered.Task, pending).WaitAsync(TestContext.Current.CancellationToken);
            if (reached == pending) await pending;
            Assert.False(pending.IsCompleted);
            Assert.Equal(blockAt, barrier.Commands.Count);
            var last = barrier.Commands.Last();
            Assert.Equal(database.Name, last["$db"].AsString);
            var commandName = operation switch
            {
                "ReadAll" or "ReadRow" => "find",
                "Delete" => "delete",
                "Cleanup" when strategy != MongoDBMembershipStrategy.SingleDocument => "delete",
                _ => "update"
            };
            Assert.True(last.Contains(commandName));
            if (transaction)
            {
                var first = barrier.Commands.First();
                var second = barrier.Commands.Last();
                Assert.True(first["startTransaction"].AsBoolean);
                Assert.Equal("snapshot", first["readConcern"]["level"].AsString);
                Assert.Equal(first["lsid"], second["lsid"]);
                Assert.Equal(first["txnNumber"], second["txnNumber"]);
                Assert.False(second["autocommit"].AsBoolean);
            }
        }
        finally
        {
            cancellation.Cancel();
            barrier.Armed = false;
            barrier.Release();
            try { await pending; }
            catch (OperationCanceledException) { } // Observed again by the assertion below, after owned transaction cleanup.
            if (transaction)
            {
                // Cancelling after wire send can leave a server transaction alive even
                // after the driver disposes its session. Abort only this test's session.
                var command = barrier.Commands.Last();
                try
                {
                    await database.Client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument
                    {
                        { "abortTransaction", 1 }, { "lsid", command["lsid"] },
                        { "txnNumber", command["txnNumber"] }, { "autocommit", false }
                    }, cancellationToken: TestContext.Current.CancellationToken);
                }
                catch (MongoCommandException abortError) when (abortError.Code == 251) { } // Already aborted by driver.
            }
        }
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(token, error.CancellationToken);
        if (operation is "ReadAll" or "ReadRow")
        {
            var after = await table.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Equal(before.Version.Version, after.Version.Version);
            Assert.Equal(before.Version.VersionEtag, after.Version.VersionEtag);
            var retained = Assert.Single(after.Members);
            Assert.Equal(row.Item2, retained.Item2);
            Assert.Equal(entry.SiloAddress, retained.Item1.SiloAddress);
            Assert.Equal("blocked", retained.Item1.HostName);
            Assert.Equal(SiloStatus.Dead, retained.Item1.Status);
            Assert.Equal(entry.IAmAliveTime, retained.Item1.IAmAliveTime);
        }
        // Writes may already have reached the server when cancellation is observed.
        // Persisted non-mutation is asserted separately for pre-cancelled operations.
    }

    private sealed class CommandBarrier(int blockAt)
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<BsonDocument> Commands { get; } = new();
        public volatile bool Armed;
        public void Release() => release.TrySetResult();

        public void OnCommand(CommandStartedEvent command)
        {
            if (!Armed || command.CommandName is not ("find" or "update" or "delete")) return;
            Commands.Enqueue(command.Command.DeepClone().AsBsonDocument);
            if (Commands.Count != blockAt) return;
            Entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        }
    }
}
