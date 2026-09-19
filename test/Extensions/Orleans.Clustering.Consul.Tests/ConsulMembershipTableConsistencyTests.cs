using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Orleans;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Membership;
using Xunit;

namespace Consul.Tests;

[TestCategory("BVT"), TestCategory("Consul")]
[TestSuite("BVT"), TestProvider("Consul"), TestArea("Membership")]
public class ConsulMembershipTableConsistencyTests
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MutationReceiptUsesMatchingNativeResultKeysAndIndependentIndices(bool update)
    {
        using var store = new ConsulHandler();
        var table = store.CreateTable(root: "membership/nested");
        var entry = Entry();
        const string prefix = "membership/nested/orleans/cluster/";
        var rowKey = prefix + entry.SiloAddress.ToParsableString();
        store.TransactionResults = results =>
        {
            Assert.Equal(2, results.Count);
            Assert.All(results, result => Assert.Null(result.Value));
            return
            [
                new(prefix + "version") { ModifyIndex = 91 },
                new(rowKey) { ModifyIndex = 73 }
            ];
        };
        if (update)
        {
            store.Set(rowKey, Encoding.UTF8.GetBytes("{}"));
        }

        var result = update
            ? await table.UpdateRowWithResultAsync(entry, "1", new TableVersion(12, "0"), CancellationToken)
            : await table.InsertRowWithResultAsync(entry, new TableVersion(12, "0"), CancellationToken);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Receipt);
        Assert.Equal(new TableVersion(12, "91"), result.Receipt.Version);
        Assert.Equal("73", result.Receipt.RowETag);
        Assert.Equal(1, store.RequestCount);
        Assert.Single(store.Transactions);
        Assert.Empty(store.Reads);
    }

    [Fact]
    public async Task MutationReceiptsChainWithoutReadsAndConflictsHaveNoReceipt()
    {
        using var store = new ConsulHandler();
        var table = store.CreateTable();
        var entry = Entry();
        var inserted = await table.InsertRowWithResultAsync(entry, new TableVersion(1, "0"), CancellationToken);
        Assert.True(inserted.Succeeded);
        var first = Assert.IsType<MembershipTableWriteReceipt>(inserted.Receipt);
        entry.Status = SiloStatus.ShuttingDown;
        var updated = await table.UpdateRowWithResultAsync(entry, first.RowETag, first.Version.Next(), CancellationToken);
        Assert.True(updated.Succeeded);
        var second = Assert.IsType<MembershipTableWriteReceipt>(updated.Receipt);
        Assert.Equal(2, second.Version.Version);
        Assert.NotEqual(first.RowETag, second.RowETag);
        Assert.NotEqual(first.Version.VersionEtag, second.Version.VersionEtag);
        var stale = await table.UpdateRowWithResultAsync(entry, first.RowETag, second.Version.Next(), CancellationToken);
        Assert.False(stale.Succeeded);
        Assert.Null(stale.Receipt);
        var duplicate = await table.InsertRowWithResultAsync(entry, second.Version.Next(), CancellationToken);
        Assert.False(duplicate.Succeeded);
        Assert.Null(duplicate.Receipt);
        var missingToken = await table.UpdateRowWithResultAsync(entry, "0", second.Version.Next(), CancellationToken);
        Assert.False(missingToken.Succeeded);
        Assert.Null(missingToken.Receipt);
        Assert.Equal(4, store.RequestCount);
        Assert.Equal(4, store.Transactions.Count);
        Assert.Empty(store.Reads);
        var snapshot = await table.ReadAllAsync(CancellationToken);
        Assert.Equal(second.Version, snapshot.Version);
        Assert.Equal(second.RowETag, snapshot.TryGet(entry.SiloAddress)!.Item2);
        Assert.Equal(entry.Status, snapshot.TryGet(entry.SiloAddress)!.Item1.Status);
    }

    [Theory]
    [InlineData(false, "empty")]
    [InlineData(false, "missing-version")]
    [InlineData(false, "duplicate")]
    [InlineData(false, "zero")]
    [InlineData(true, "empty")]
    [InlineData(true, "missing-version")]
    [InlineData(true, "duplicate")]
    [InlineData(true, "zero")]
    public async Task MutationMalformedSuccessFailsWithoutFallbackRead(bool update, string kind)
    {
        using var store = new ConsulHandler();
        var table = store.CreateTable();
        var entry = Entry();
        if (update)
        {
            store.Seed(entry, heartbeat: false);
        }

        store.TransactionResults = results => kind switch
        {
            "empty" => [],
            "missing-version" => [results[0]],
            "duplicate" => [results[0], results[0], results[1]],
            "zero" => [new(results[0].Key), results[1]],
            _ => throw new InvalidOperationException(kind)
        };
        var failure = await Assert.ThrowsAsync<OrleansException>(() => update
            ? table.UpdateRowWithResultAsync(entry, "1", new TableVersion(1, "0"), CancellationToken)
            : table.InsertRowWithResultAsync(entry, new TableVersion(1, "0"), CancellationToken));
        Assert.Contains("commit metadata", failure.Message);
        Assert.Equal(1, store.RequestCount);
        Assert.Single(store.Transactions);
        Assert.Empty(store.Reads);
    }

    [Theory]
    [InlineData("insert")]
    [InlineData("update")]
    public async Task ReceiptWritesPreCanceledIssueNoRequests(string operation)
    {
        using var store = new ConsulHandler();
        var table = store.CreateTable();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        cancellation.Cancel();
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation switch
        {
            "insert" => table.InsertRowWithResultAsync(Entry(), new TableVersion(1, "0"), cancellation.Token),
            "update" => table.UpdateRowWithResultAsync(Entry(), "1", new TableVersion(1, "0"), cancellation.Token),
            _ => throw new InvalidOperationException(operation)
        });
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Equal(0, store.RequestCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("membership/nested")]
    public async Task InitializationUsesOneNativeCASAndPreservesExistingVersion(string? root)
    {
        using var store = new ConsulHandler();
        var table = store.CreateTable(root: root);
        await table.InitializeMembershipTableAsync(false, CancellationToken);
        Assert.Equal(0, store.RequestCount);
        await table.InitializeMembershipTableAsync(true, CancellationToken);
        Assert.Equal(1, store.RequestCount);
        Assert.Empty(store.Reads);
        Assert.Empty(store.Transactions);
        Assert.Empty(store.Puts);
        var operation = Assert.Single(store.CompareAndSets);
        Assert.Equal(root is null ? "orleans/cluster/version" : $"{root}/orleans/cluster/version", operation.Key);
        Assert.Equal(0UL, operation.ModifyIndex);
        Assert.Equal(Encoding.UTF8.GetBytes("0"), operation.Value);

        var version = (await table.ReadAllAsync(CancellationToken)).Version;
        Assert.True(await table.InsertRowAsync(Entry(), version.Next(), CancellationToken));
        var before = store.Snapshot();
        var requests = store.RequestCount;
        await table.InitializeMembershipTableAsync(true, CancellationToken);
        Assert.Equal(requests + 1, store.RequestCount);
        Assert.Equal(2, store.CompareAndSets.Count);
        Assert.Equal(before, store.Snapshot());
    }

    [Fact]
    public async Task InitializationSurfacesUnexpectedNotFoundResponse()
    {
        using var store = new ConsulHandler();
        var table = store.CreateTable();
        store.FailureMethod = HttpMethod.Put;
        store.FailureStatus = HttpStatusCode.NotFound;
        var exception = await Assert.ThrowsAsync<OrleansException>(() => table.InitializeMembershipTableAsync(true, CancellationToken));
        Assert.Contains("NotFound", exception.Message);
        Assert.Equal(1, store.RequestCount);
        Assert.Empty(store.Keys);
    }

    [Fact]
    public async Task ReadsAssociateHeartbeatsFromOneSnapshot()
    {
        using var store = new ConsulHandler();
        var table = store.CreateTable();
        var expected = new Dictionary<SiloAddress, DateTime>();
        for (var i = 1; i <= 50; i++)
        {
            var entry = Entry(i);
            entry.IAmAliveTime = Epoch.AddMinutes(i);
            var heartbeat = i % 3 != 0;
            store.Seed(entry, heartbeat);
            expected.Add(entry.SiloAddress, heartbeat ? entry.IAmAliveTime : entry.StartTime);
        }

        await table.InitializeMembershipTableAsync(true, CancellationToken);
        var requests = store.RequestCount;
        var result = await table.ReadAllAsync(CancellationToken);
        Assert.Equal(requests + 1, store.RequestCount);
        Assert.Equal(expected.Count, result.Members.Count);
        Assert.All(result.Members, row => Assert.Equal(expected[row.Item1.SiloAddress], row.Item1.IAmAliveTime));
        Assert.Equal(0, result.Version.Version);
        Assert.NotEqual("0", result.Version.VersionEtag);
        Assert.Empty(store.Transactions);
    }

    [Fact]
    public async Task CleanupUsesOneSnapshotAndIsolatesConflictingCandidates()
    {
        using var store = new ConsulHandler();
        var table = store.CreateTable();
        for (var i = 1; i <= 10; i++)
        {
            var entry = Entry(i);
            entry.Status = SiloStatus.Dead;
            store.Seed(entry, heartbeat: i % 2 == 0);
        }

        var survivor = Entry(20);
        store.Seed(survivor);
        await table.InitializeMembershipTableAsync(true, CancellationToken);
        var version = (await table.ReadAllAsync(CancellationToken)).Version;
        MembershipEntry? refreshed = null;
        store.BeforeTransaction = () =>
        {
            var key = Assert.Single(store.Transactions).First().Key;
            refreshed = Entry();
            refreshed.SiloAddress = SiloAddress.FromParsableString(key["orleans/cluster/".Length..]);
            refreshed.Status = SiloStatus.Dead;
            refreshed.SuspectTimes = new() { Tuple.Create(survivor.SiloAddress, Epoch.AddDays(2)) };
            store.Seed(refreshed);
            return Task.CompletedTask;
        };

        var reads = store.Reads.Count;
        var requests = store.RequestCount;
        await table.CleanupDefunctSiloEntriesAsync(Epoch.AddDays(1), CancellationToken);
        Assert.Equal(reads + 1, store.Reads.Count);
        Assert.Equal(requests + 11, store.RequestCount);
        Assert.Equal(10, store.Transactions.Count);
        Assert.Equal(1, store.ConflictCount);
        Assert.All(store.Transactions, operations =>
        {
            Assert.Equal(2, operations.Count);
            Assert.All(operations, operation => Assert.Equal(KVTxnVerb.DeleteCAS, operation.Verb));
        });
        Assert.NotNull(refreshed);
        var result = await table.ReadAllAsync(CancellationToken);
        Assert.Equal(version, result.Version);
        Assert.Equal(2, result.Members.Count);
        Assert.NotNull(result.TryGet(survivor.SiloAddress));
        Assert.Equal(refreshed.SuspectTimes, result.TryGet(refreshed.SiloAddress)!.Item1.SuspectTimes);
    }

    [Theory]
    [InlineData("insert", "permission")]
    [InlineData("insert", "storage")]
    [InlineData("insert", "mixed")]
    [InlineData("insert", "empty")]
    [InlineData("update", "permission")]
    [InlineData("update", "storage")]
    [InlineData("update", "mixed")]
    [InlineData("update", "empty")]
    [InlineData("insert-result", "permission")]
    [InlineData("insert-result", "storage")]
    [InlineData("insert-result", "mixed")]
    [InlineData("insert-result", "empty")]
    [InlineData("update-result", "permission")]
    [InlineData("update-result", "storage")]
    [InlineData("update-result", "mixed")]
    [InlineData("update-result", "empty")]
    [InlineData("cleanup", "permission")]
    [InlineData("cleanup", "storage")]
    [InlineData("cleanup", "mixed")]
    [InlineData("cleanup", "empty")]
    public async Task TransactionFailuresSurfaceNativeOperationErrors(string operation, string failure)
    {
        using var store = new ConsulHandler();
        var table = store.CreateTable();
        var entry = Entry();
        entry.Status = SiloStatus.Dead;
        store.Seed(entry);
        var first = await table.ReadAllAsync(CancellationToken);
        var before = store.Snapshot();
        var what = failure == "storage" ? "failed kvs lookup: injected storage failure"
            : "Permission denied: token with AccessorID 'test' lacks permission 'key:write' on 'orleans/cluster'";
        store.TransactionErrors = failure switch
        {
            "empty" => [],
            "mixed" =>
            [
                (0, $"failed to {(operation == "cleanup" ? "delete" : "set")} key \"orleans/cluster/silo\", index is stale"),
                (1, what)
            ],
            _ => [(0, what)]
        };

        var requests = store.RequestCount;
        var exception = await Assert.ThrowsAsync<OrleansException>(() => operation switch
        {
            "insert" => table.InsertRowAsync(Entry(2), first.Version.Next(), CancellationToken),
            "update" => table.UpdateRowAsync(entry, Assert.Single(first.Members).Item2, first.Version.Next(), CancellationToken),
            "insert-result" => table.InsertRowWithResultAsync(Entry(2), first.Version.Next(), CancellationToken),
            "update-result" => table.UpdateRowWithResultAsync(entry, Assert.Single(first.Members).Item2, first.Version.Next(), CancellationToken),
            "cleanup" => table.CleanupDefunctSiloEntriesAsync(Epoch.AddDays(1), CancellationToken),
            _ => throw new InvalidOperationException(operation)
        });
        Assert.Contains("Consul membership transaction failed", exception.Message);
        if (failure != "empty")
        {
            Assert.Contains(what, exception.Message);
        }

        Assert.Equal(requests + (operation == "cleanup" ? 2 : 1), store.RequestCount);
        Assert.Single(store.Transactions);
        Assert.Equal(before, store.Snapshot());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("membership/nested")]
    public async Task ReadsReturnOneConsistentClusterSnapshot(string? root)
    {
        using var store = new ConsulHandler();
        var table = store.CreateTable("cluster", root);
        var neighbor = store.CreateTable("cluster-other", root);
        var entry = Entry();
        await table.InitializeMembershipTableAsync(true, CancellationToken);
        await table.InitializeMembershipTableAsync(true, CancellationToken);
        var initial = await table.ReadAllAsync(CancellationToken);
        Assert.Equal(0, initial.Version.Version);
        Assert.NotEqual("0", initial.Version.VersionEtag);
        Assert.True(await table.InsertRowAsync(entry, initial.Version.Next(), CancellationToken));
        Assert.True(await neighbor.InsertRowAsync(Entry(2), new TableVersion(1, "0"), CancellationToken));

        var requests = store.RequestCount;
        var all = await table.ReadAllAsync(CancellationToken);
        Assert.Equal(requests + 1, store.RequestCount);
        Assert.Equal(entry.SiloAddress, Assert.Single(all.Members).Item1.SiloAddress);
        Assert.Equal(entry.StartTime, all.Members[0].Item1.IAmAliveTime);
        Assert.Equal(1, all.Version.Version);

        requests = store.RequestCount;
        var reads = store.Reads.Count;
        var row = await table.ReadRowAsync(entry.SiloAddress, CancellationToken);
        Assert.Equal(requests + 3, store.RequestCount);
        var prefix = root is null ? "orleans/cluster" : $"{root}/orleans/cluster";
        Assert.Equal(new[]
        {
            (prefix + "/version", false),
            ($"{prefix}/{entry.SiloAddress.ToParsableString()}", true),
            (prefix + "/version", false)
        }, store.Reads.Skip(reads));
        Assert.Equal(all.Version, row.Version);
        Assert.Equal(all.Members[0].Item2, Assert.Single(row.Members).Item2);
        var missing = await table.ReadRowAsync(Entry(3).SiloAddress, CancellationToken);
        Assert.Empty(missing.Members);
        Assert.Equal(all.Version, missing.Version);
        var gatewayView = await ConsulBasedMembershipTable.ReadAllAsync(
            store.Client, "cluster", root, NullLogger.Instance, null, CancellationToken);
        Assert.Equal(all.Version, gatewayView.Version);
        Assert.Equal(entry.SiloAddress, Assert.Single(gatewayView.Members).Item1.SiloAddress);
    }

    [Fact]
    public async Task RowReadsStayLocalAndStatusUpdatesNeedNoReads()
    {
        using var store = new ConsulHandler();
        var table = store.CreateTable();
        var entry = Entry();
        for (var i = 1; i <= 50; i++)
        {
            store.Seed(Entry(i));
        }

        var first = await table.ReadRowAsync(entry.SiloAddress, CancellationToken);
        var row = Assert.Single(first.Members);
        Assert.Equal(entry.SiloAddress, row.Item1.SiloAddress);
        entry.Status = SiloStatus.ShuttingDown;
        Assert.True(await table.UpdateRowAsync(entry, row.Item2, first.Version.Next(), CancellationToken));
        Assert.Equal(3, store.Reads.Count);
        Assert.All(store.Reads, read => Assert.Equal(
            read.Recursive ? $"orleans/cluster/{entry.SiloAddress.ToParsableString()}" : "orleans/cluster/version", read.Key));
        Assert.Equal(2, store.ReturnedKeyCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RowReadRetriesWhenVersionChangesAroundSnapshot(bool beforeRowRead)
    {
        using var store = new ConsulHandler();
        var table = store.CreateTable();
        var otherWriter = store.CreateTable();
        var entry = Entry();
        store.Seed(entry);
        var first = await table.ReadRowAsync(entry.SiloAddress, CancellationToken);
        var updated = false;
        store.AfterRead = async (_, recursive) =>
        {
            if (recursive == beforeRowRead)
            {
                return;
            }

            store.AfterRead = null;
            entry.Status = SiloStatus.ShuttingDown;
            Assert.True(await otherWriter.UpdateRowAsync(entry, Assert.Single(first.Members).Item2, first.Version.Next(), CancellationToken));
            updated = true;
        };

        var result = await table.ReadRowAsync(entry.SiloAddress, CancellationToken);
        Assert.True(updated);
        Assert.Equal(1, result.Version.Version);
        var row = Assert.Single(result.Members);
        Assert.Equal(entry.Status, row.Item1.Status);
        Assert.Equal(entry.IAmAliveTime, row.Item1.IAmAliveTime);
        Assert.NotEqual(Assert.Single(first.Members).Item2, row.Item2);
        Assert.Equal((await otherWriter.ReadAllAsync(CancellationToken)).Version, result.Version);
    }

    [Fact]
    public async Task RowReadPropagatesVersionValidationFailure()
    {
        using var store = new ConsulHandler();
        var table = store.CreateTable();
        store.Seed(Entry());
        store.AfterRead = (_, recursive) =>
        {
            if (recursive)
            {
                store.FailureMethod = HttpMethod.Get;
            }

            return Task.CompletedTask;
        };

        await Assert.ThrowsAsync<ConsulRequestException>(() => table.ReadRowAsync(Entry().SiloAddress, CancellationToken));
        Assert.Equal(3, store.RequestCount);
    }

    [Fact]
    public async Task InsertAndUpdateAtomicallyRejectStaleRowOrTableTokens()
    {
        using var store = new ConsulHandler();
        var table = store.CreateTable();
        var otherWriter = store.CreateTable();
        var entry = Entry();
        Assert.True(await table.InsertRowAsync(entry, new TableVersion(1, "0"), CancellationToken));
        var first = await table.ReadAllAsync(CancellationToken);
        var original = Assert.Single(first.Members);
        var before = store.Snapshot();
        Assert.False(await table.InsertRowAsync(entry, first.Version.Next(), CancellationToken));
        Assert.False(await table.InsertRowAsync(Entry(2), new TableVersion(1, "0"), CancellationToken));
        Assert.Equal(before, store.Snapshot());

        entry.Status = SiloStatus.ShuttingDown;
        store.BeforeTransaction = async () =>
        {
            Assert.True(await otherWriter.InsertRowAsync(Entry(2), first.Version.Next(), CancellationToken));
        };
        Assert.False(await table.UpdateRowAsync(entry, original.Item2, first.Version.Next(), CancellationToken));
        var after = await table.ReadAllAsync(CancellationToken);
        Assert.Equal(2, after.Members.Count);
        Assert.Equal(2, after.Version.Version);
        var unchanged = after.Members.Single(row => row.Item1.SiloAddress.Equals(entry.SiloAddress));
        Assert.Equal(SiloStatus.Active, unchanged.Item1.Status);
        Assert.Equal(original.Item2, unchanged.Item2);
        Assert.Equal(original.Item1.IAmAliveTime, unchanged.Item1.IAmAliveTime);

        Assert.True(await table.UpdateRowAsync(entry, original.Item2, after.Version.Next(), CancellationToken));
        var updated = await table.ReadAllAsync(CancellationToken);
        before = store.Snapshot();
        Assert.False(await table.UpdateRowAsync(entry, original.Item2, updated.Version.Next(), CancellationToken));
        Assert.False(await table.UpdateRowAsync(Entry(3), "0", updated.Version.Next(), CancellationToken));
        Assert.Equal(before, store.Snapshot());
        Assert.Equal(3, updated.Version.Version);
        var changed = updated.Members.Single(row => row.Item1.SiloAddress.Equals(entry.SiloAddress));
        Assert.Equal(SiloStatus.ShuttingDown, changed.Item1.Status);
        Assert.NotEqual(original.Item2, changed.Item2);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OwnerHeartbeatLeavesCanonicalTokensUsable(bool updateVotes, bool duringTransaction)
    {
        using var store = new ConsulHandler();
        var table = store.CreateTable();
        var otherWriter = store.CreateTable();
        var entry = Entry();
        Assert.True(await table.InsertRowAsync(entry, new TableVersion(1, "0"), CancellationToken));
        var first = await table.ReadAllAsync(CancellationToken);
        var original = Assert.Single(first.Members);
        var later = Entry();
        later.IAmAliveTime = Epoch.AddHours(4);
        if (duringTransaction)
        {
            store.BeforeTransaction = () => otherWriter.UpdateIAmAliveAsync(later, CancellationToken);
        }
        else
        {
            await otherWriter.UpdateIAmAliveAsync(later, CancellationToken);
            var afterHeartbeat = await table.ReadRowAsync(entry.SiloAddress, CancellationToken);
            Assert.Equal(first.Version, afterHeartbeat.Version);
            Assert.Equal(original.Item2, Assert.Single(afterHeartbeat.Members).Item2);
        }

        if (updateVotes)
        {
            entry.SuspectTimes = new() { Tuple.Create(Entry(2).SiloAddress, Epoch.AddHours(3)) };
        }
        else
        {
            entry.Status = SiloStatus.ShuttingDown;
        }

        entry.IAmAliveTime = Epoch.AddHours(8);
        var reads = store.Reads.Count;
        var requests = store.RequestCount;
        var transactions = store.Transactions.Count;
        Assert.True(await table.UpdateRowAsync(entry, original.Item2, first.Version.Next(), CancellationToken));
        Assert.Equal(reads, store.Reads.Count);
        Assert.Equal(requests + (duringTransaction ? 2 : 1), store.RequestCount);
        var operations = Assert.Single(store.Transactions.Skip(transactions));
        Assert.Equal(new[] { $"orleans/cluster/{entry.SiloAddress.ToParsableString()}", "orleans/cluster/version" },
            operations.Select(operation => operation.Key));
        Assert.All(operations, operation => Assert.Equal(KVTxnVerb.CAS, operation.Verb));
        var updated = await table.ReadAllAsync(CancellationToken);
        var row = Assert.Single(updated.Members);
        Assert.Equal(later.IAmAliveTime, row.Item1.IAmAliveTime);
        Assert.Equal(entry.Status, row.Item1.Status);
        Assert.Equal(entry.SuspectTimes, row.Item1.SuspectTimes);
        Assert.Equal(2, updated.Version.Version);
        Assert.Equal(0, store.ConflictCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanonicalInsertIgnoresExistingHeartbeatKey(bool existingHeartbeat)
    {
        using var store = new ConsulHandler();
        var table = store.CreateTable();
        var entry = Entry();
        var rowKey = $"orleans/cluster/{entry.SiloAddress.ToParsableString()}";
        var heartbeatKey = rowKey + "/iamalive";
        var value = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(Epoch.AddHours(2)));
        if (existingHeartbeat)
        {
            store.Set(heartbeatKey, value);
        }

        Assert.True(await table.InsertRowAsync(entry, new TableVersion(1, "0"), CancellationToken));
        Assert.Equal(1, store.RequestCount);
        Assert.Empty(store.Reads);
        Assert.Empty(store.Puts);
        var operations = Assert.Single(store.Transactions);
        Assert.Equal(new[] { rowKey, "orleans/cluster/version" }, operations.Select(operation => operation.Key));
        Assert.All(operations, operation => Assert.Equal(KVTxnVerb.CAS, operation.Verb));
        var heartbeat = (await store.Client.KV.Get(heartbeatKey, new QueryOptions { Consistency = ConsistencyMode.Consistent }, CancellationToken)).Response;
        if (existingHeartbeat)
        {
            Assert.Equal(value, heartbeat.Value);
            Assert.Equal(1UL, heartbeat.ModifyIndex);
        }
        else
        {
            Assert.Null(heartbeat);
        }
    }

    [Fact]
    public async Task OwnerHeartbeatChangesDoNotRetryCanonicalReadFence()
    {
        using var store = new ConsulHandler();
        var table = store.CreateTable();
        var entry = Entry();
        store.Seed(entry);
        var first = await table.ReadRowAsync(entry.SiloAddress, CancellationToken);
        var publications = 0;
        store.AfterRead = async (_, _) =>
        {
            entry.IAmAliveTime = Epoch.AddHours(++publications + 1);
            await table.UpdateIAmAliveAsync(entry, CancellationToken);
        };

        var requests = store.RequestCount;
        var reads = store.Reads.Count;
        var result = await table.ReadRowAsync(entry.SiloAddress, CancellationToken);
        store.AfterRead = null;
        Assert.Equal(requests + 6, store.RequestCount);
        Assert.Equal(reads + 3, store.Reads.Count);
        Assert.Equal(3, publications);
        Assert.Equal(first.Version, result.Version);
        var row = Assert.Single(result.Members);
        Assert.Equal(Assert.Single(first.Members).Item2, row.Item2);
        Assert.Equal(Epoch.AddHours(2), row.Item1.IAmAliveTime);
        Assert.Equal(Epoch.AddHours(4), Assert.Single((await table.ReadRowAsync(entry.SiloAddress, CancellationToken)).Members).Item1.IAmAliveTime);
        Assert.Equal(0, store.ConflictCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StatusUpdateRacingRowChangeOrPruneIsRejected(bool prune)
    {
        using var store = new ConsulHandler();
        var table = store.CreateTable();
        var otherWriter = store.CreateTable();
        var entry = Entry();
        entry.Status = SiloStatus.Dead;
        Assert.True(await table.InsertRowAsync(entry, new TableVersion(1, "0"), CancellationToken));
        var first = await table.ReadAllAsync(CancellationToken);
        var original = Assert.Single(first.Members);
        string? committed = null;
        store.BeforeTransaction = async () =>
        {
            if (prune)
            {
                await otherWriter.CleanupDefunctSiloEntriesAsync(Epoch.AddDays(1), CancellationToken);
            }
            else
            {
                var winner = Entry();
                Assert.True(await otherWriter.UpdateRowAsync(winner, original.Item2, first.Version.Next(), CancellationToken));
            }

            committed = store.Snapshot();
        };
        entry.Status = SiloStatus.ShuttingDown;
        Assert.False(await table.UpdateRowAsync(entry, original.Item2, first.Version.Next(), CancellationToken));
        Assert.Equal(committed, store.Snapshot());
        Assert.Equal(1, store.ConflictCount);
        var result = await table.ReadAllAsync(CancellationToken);
        if (prune)
        {
            Assert.Empty(result.Members);
            Assert.Equal(first.Version, result.Version);
        }
        else
        {
            var winner = Assert.Single(result.Members);
            Assert.Equal(SiloStatus.Active, winner.Item1.Status);
            Assert.Equal(entry.StartTime, winner.Item1.IAmAliveTime);
            Assert.Equal(2, result.Version.Version);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("membership/nested")]
    public async Task OwnerHeartbeatUsesOneBlindWriteAndPreservesOtherFields(string? root)
    {
        using var store = new ConsulHandler();
        var table = store.CreateTable(root: root);
        var entry = Entry();
        entry.ProxyPort = 30000;
        entry.SuspectTimes = new() { Tuple.Create(Entry(2).SiloAddress, Epoch) };
        Assert.True(await table.InsertRowAsync(entry, new TableVersion(1, "0"), CancellationToken));
        Assert.True(await table.InsertRowAsync(Entry(2), (await table.ReadAllAsync(CancellationToken)).Version.Next(), CancellationToken));
        var first = await table.ReadAllAsync(CancellationToken);
        var original = first.Members.Single(row => row.Item1.SiloAddress.Equals(entry.SiloAddress));
        var prefix = root is null ? "orleans/cluster" : $"{root}/orleans/cluster";
        var heartbeatKey = $"{prefix}/{entry.SiloAddress.ToParsableString()}/iamalive";
        var otherFields = store.Snapshot(excludedKey: heartbeatKey);
        foreach (var time in new[] { Epoch.AddHours(2), Epoch.AddHours(2), Epoch.AddHours(3) })
        {
            var requests = store.RequestCount;
            var reads = store.Reads.Count;
            var transactions = store.Transactions.Count;
            var puts = store.Puts.Count;
            await table.UpdateIAmAliveAsync(new MembershipEntry { SiloAddress = entry.SiloAddress, IAmAliveTime = time }, CancellationToken);
            Assert.Equal(requests + 1, store.RequestCount);
            Assert.Equal(reads, store.Reads.Count);
            Assert.Equal(transactions, store.Transactions.Count);
            var put = Assert.Single(store.Puts.Skip(puts));
            Assert.Equal(heartbeatKey, put.Key);
            Assert.Equal(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(time)), put.Value);
            Assert.Equal(otherFields, store.Snapshot(excludedKey: heartbeatKey));
        }

        var result = await table.ReadAllAsync(CancellationToken);
        Assert.Equal(first.Version, result.Version);
        var updated = result.Members.Single(row => row.Item1.SiloAddress.Equals(entry.SiloAddress));
        Assert.Equal(original.Item2, updated.Item2);
        Assert.Equal(Epoch.AddHours(3), updated.Item1.IAmAliveTime);
        Assert.Equal(entry.ProxyPort, updated.Item1.ProxyPort);
        Assert.Equal(entry.Status, updated.Item1.Status);
        Assert.Equal(entry.SuspectTimes, updated.Item1.SuspectTimes);
        Assert.Equal(0, store.ConflictCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnerHeartbeatHonorsCancellation(bool cancelBeforeWrite)
    {
        using var store = new ConsulHandler();
        var table = store.CreateTable();
        var entry = Entry();
        store.Seed(entry);
        var before = store.Snapshot();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        if (cancelBeforeWrite)
        {
            cancellation.Cancel();
        }
        else
        {
            store.BeforePut = token =>
            {
                Assert.True(token.CanBeCanceled);
                cancellation.Cancel();
                Assert.True(token.IsCancellationRequested);
                return Task.CompletedTask;
            };
        }

        entry.IAmAliveTime = Epoch.AddHours(2);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => table.UpdateIAmAliveAsync(entry, cancellation.Token));
        Assert.Equal(cancelBeforeWrite ? 0 : 1, store.RequestCount);
        Assert.Equal(cancelBeforeWrite ? 0 : 1, store.Puts.Count);
        Assert.Empty(store.Reads);
        Assert.Empty(store.Transactions);
        Assert.Equal(before, store.Snapshot());
    }

    [Fact]
    public async Task OwnerHeartbeatSurfacesFalseWriteResponse()
    {
        using var store = new ConsulHandler();
        var table = store.CreateTable();
        var entry = Entry();
        store.Seed(entry);
        var before = store.Snapshot();
        store.PutResult = false;
        entry.IAmAliveTime = Epoch.AddHours(2);
        await Assert.ThrowsAsync<OrleansException>(() => table.UpdateIAmAliveAsync(entry, CancellationToken));
        Assert.Equal(1, store.RequestCount);
        Assert.Single(store.Puts);
        Assert.Empty(store.Reads);
        Assert.Empty(store.Transactions);
        Assert.Equal(before, store.Snapshot());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupPrunesOnlyOldDeadRowsWithoutChangingVersion(bool legacy)
    {
        using var store = new ConsulHandler();
        var table = store.CreateTable();
        var cutoff = Epoch.AddDays(1);
        var expected = new List<SiloAddress>();
        var id = 0;
        foreach (var status in Enum.GetValues<SiloStatus>())
        {
            var entry = Entry(++id);
            entry.Status = status;
            store.Seed(entry, heartbeat: !legacy);
            if (status != SiloStatus.Dead)
            {
                expected.Add(entry.SiloAddress);
            }
        }

        foreach (var field in new[] { "start", "heartbeat", "vote" })
        {
            foreach (var offset in new[] { -1, 0, 1 })
            {
                var entry = Entry(++id);
                entry.Status = SiloStatus.Dead;
                var time = cutoff.AddTicks(offset);
                if (field == "start")
                {
                    entry.StartTime = time;
                }
                else if (field == "heartbeat")
                {
                    entry.IAmAliveTime = time;
                }
                else
                {
                    entry.SuspectTimes = new() { Tuple.Create(Entry(100).SiloAddress, time) };
                }

                store.Seed(entry, heartbeat: field == "heartbeat" || !legacy);
                if (offset >= 0)
                {
                    expected.Add(entry.SiloAddress);
                }
            }
        }

        if (!legacy)
        {
            await table.InitializeMembershipTableAsync(true, CancellationToken);
        }

        var version = (await table.ReadAllAsync(CancellationToken)).Version;
        var transactionStart = store.Transactions.Count;
        await table.CleanupDefunctSiloEntriesAsync(new DateTimeOffset(cutoff).ToOffset(TimeSpan.FromHours(3)), CancellationToken);
        var result = await table.ReadAllAsync(CancellationToken);
        Assert.Equal(version, result.Version);
        Assert.Equal(expected.OrderBy(address => address.ToParsableString()),
            result.Members.Select(row => row.Item1.SiloAddress).OrderBy(address => address.ToParsableString()));
        Assert.All(store.Transactions.Skip(transactionStart), operations =>
        {
            Assert.Equal(2, operations.Count);
            Assert.All(operations, operation => Assert.Equal(KVTxnVerb.DeleteCAS, operation.Verb));
            Assert.DoesNotContain(operations, operation => operation.Key.EndsWith("/version", StringComparison.Ordinal));
        });
        Assert.All(store.Keys.Where(key => key.EndsWith("/iamalive", StringComparison.Ordinal)),
            key => Assert.Contains(key[..^"/iamalive".Length], store.Keys));
    }

    [Theory]
    [InlineData("heartbeat", false)]
    [InlineData("heartbeat", true)]
    [InlineData("status", false)]
    [InlineData("vote", false)]
    public async Task CleanupCASPreservesConcurrentUpdates(string update, bool legacy)
    {
        using var store = new ConsulHandler();
        var table = store.CreateTable();
        var otherWriter = store.CreateTable();
        var entry = Entry();
        entry.Status = SiloStatus.Dead;
        store.Seed(entry, heartbeat: !legacy);
        var first = await table.ReadAllAsync(CancellationToken);
        var original = Assert.Single(first.Members);
        store.BeforeTransaction = async () =>
        {
            if (update == "heartbeat")
            {
                entry.IAmAliveTime = Epoch.AddDays(2);
                var heartbeat = new KVPair($"orleans/cluster/{entry.SiloAddress.ToParsableString()}/iamalive")
                {
                    Value = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(entry.IAmAliveTime))
                };
                Assert.True((await store.Client.KV.Put(heartbeat, CancellationToken)).Response);
            }
            else
            {
                if (update == "status")
                {
                    entry.Status = SiloStatus.Active;
                }
                else
                {
                    entry.SuspectTimes = new() { Tuple.Create(Entry(2).SiloAddress, Epoch.AddDays(2)) };
                }

                Assert.True(await otherWriter.UpdateRowAsync(entry, original.Item2, first.Version.Next(), CancellationToken));
            }
        };
        await table.CleanupDefunctSiloEntriesAsync(Epoch.AddDays(1), CancellationToken);
        var result = await table.ReadAllAsync(CancellationToken);
        var survivor = Assert.Single(result.Members);
        Assert.Equal(entry.Status, survivor.Item1.Status);
        Assert.Equal(entry.IAmAliveTime, survivor.Item1.IAmAliveTime);
        Assert.Equal(entry.SuspectTimes, survivor.Item1.SuspectTimes);
        Assert.Equal(update == "heartbeat" ? first.Version.Version : first.Version.Version + 1, result.Version.Version);
        Assert.Equal(1, store.ConflictCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateStatusUpdateCannotRecreatePrunedRowOrVersion(bool legacy)
    {
        using var store = new ConsulHandler();
        var table = store.CreateTable();
        var entry = Entry();
        entry.Status = SiloStatus.Dead;
        store.Seed(entry, heartbeat: !legacy);
        if (!legacy)
        {
            await table.InitializeMembershipTableAsync(true, CancellationToken);
        }

        var first = await table.ReadAllAsync(CancellationToken);
        await table.CleanupDefunctSiloEntriesAsync(Epoch.AddDays(1), CancellationToken);
        entry.IAmAliveTime = Epoch.AddDays(2);
        Assert.False(await table.UpdateRowAsync(entry, Assert.Single(first.Members).Item2, first.Version.Next(), CancellationToken));
        var result = await table.ReadAllAsync(CancellationToken);
        Assert.Empty(result.Members);
        Assert.Equal(first.Version, result.Version);
        Assert.Equal(legacy ? Array.Empty<string>() : new[] { "orleans/cluster/version" }, store.Keys);
        Assert.Equal(1, store.ConflictCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("membership/nested")]
    public async Task CleanupAndDeleteRespectClusterPrefixAndRequestedCluster(string? root)
    {
        using var store = new ConsulHandler();
        var table = store.CreateTable("cluster", root);
        var neighbor = store.CreateTable("cluster-other", root);
        var dead = Entry();
        dead.Status = SiloStatus.Dead;
        Assert.True(await table.InsertRowAsync(dead, new TableVersion(1, "0"), CancellationToken));
        Assert.True(await neighbor.InsertRowAsync(dead, new TableVersion(1, "0"), CancellationToken));
        await table.CleanupDefunctSiloEntriesAsync(Epoch.AddDays(1), CancellationToken);
        Assert.Empty((await table.ReadAllAsync(CancellationToken)).Members);
        Assert.Single((await neighbor.ReadAllAsync(CancellationToken)).Members);

        Assert.True(await table.InsertRowAsync(Entry(2), (await table.ReadAllAsync(CancellationToken)).Version.Next(), CancellationToken));
        var before = await table.ReadAllAsync(CancellationToken);
        await table.DeleteMembershipTableEntriesAsync("cluster-other", CancellationToken);
        Assert.Empty((await neighbor.ReadAllAsync(CancellationToken)).Members);
        var after = await table.ReadAllAsync(CancellationToken);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(Entry(2).SiloAddress, Assert.Single(after.Members).Item1.SiloAddress);
    }

    [Theory]
    [InlineData("initialize")]
    [InlineData("read-all")]
    [InlineData("read-row")]
    [InlineData("insert")]
    [InlineData("update")]
    [InlineData("heartbeat")]
    [InlineData("cleanup")]
    [InlineData("delete")]
    public async Task BackendFailuresPropagate(string operation)
    {
        using var store = new ConsulHandler();
        var table = store.CreateTable();
        var entry = Entry();
        entry.Status = operation == "cleanup" ? SiloStatus.Dead : SiloStatus.Active;
        Assert.True(await table.InsertRowAsync(entry, new TableVersion(1, "0"), CancellationToken));
        var first = await table.ReadAllAsync(CancellationToken);
        var before = store.Snapshot();
        var requests = store.RequestCount;
        entry.IAmAliveTime = Epoch.AddHours(2);
        store.FailureMethod = operation is "read-all" or "read-row" ? HttpMethod.Get
            : operation == "delete" ? HttpMethod.Delete : HttpMethod.Put;
        var exception = await Assert.ThrowsAsync<ConsulRequestException>(() => operation switch
        {
            "initialize" => table.InitializeMembershipTableAsync(true, CancellationToken),
            "read-all" => table.ReadAllAsync(CancellationToken),
            "read-row" => table.ReadRowAsync(entry.SiloAddress, CancellationToken),
            "insert" => table.InsertRowAsync(Entry(2), first.Version.Next(), CancellationToken),
            "update" => table.UpdateRowAsync(entry, Assert.Single(first.Members).Item2, first.Version.Next(), CancellationToken),
            "heartbeat" => table.UpdateIAmAliveAsync(entry, CancellationToken),
            "cleanup" => table.CleanupDefunctSiloEntriesAsync(Epoch.AddDays(1), CancellationToken),
            "delete" => table.DeleteMembershipTableEntriesAsync("cluster", CancellationToken),
            _ => throw new InvalidOperationException(operation)
        });
        Assert.Contains("injected failure", exception.Message);
        Assert.Equal(before, store.Snapshot());
        if (operation == "heartbeat")
        {
            Assert.Equal(requests + 1, store.RequestCount);
        }
    }

    [Fact]
    public async Task DeleteFailureResponseAndMalformedVersionAreSurfaced()
    {
        using var store = new ConsulHandler();
        var table = store.CreateTable();
        store.DeleteResult = false;
        await Assert.ThrowsAsync<OrleansException>(() => table.DeleteMembershipTableEntriesAsync("cluster", CancellationToken));
        store.Set("orleans/cluster/version", Encoding.UTF8.GetBytes("invalid"));
        await Assert.ThrowsAsync<FormatException>(() => table.ReadAllAsync(CancellationToken));
        await Assert.ThrowsAsync<FormatException>(() => table.ReadRowAsync(Entry().SiloAddress, CancellationToken));
        await Assert.ThrowsAsync<FormatException>(() => table.InsertRowAsync(Entry(), new TableVersion(1, "invalid"), CancellationToken));
    }

    [Fact]
    public async Task StatusTransactionHonorsCancellation()
    {
        using var store = new ConsulHandler();
        var table = store.CreateTable();
        var entry = Entry();
        store.Seed(entry);
        var first = await table.ReadRowAsync(entry.SiloAddress, CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        store.BeforeTransaction = () =>
        {
            store.Seed(entry);
            cancellation.Cancel();
            return Task.CompletedTask;
        };
        entry.IAmAliveTime = Epoch.AddHours(2);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            table.UpdateRowAsync(entry, Assert.Single(first.Members).Item2, first.Version.Next(), cancellation.Token));
        Assert.Single(store.Transactions);
    }

    [Fact]
    public async Task StatusTransactionReturnsCanonicalConflictWithoutRetrying()
    {
        using var store = new ConsulHandler();
        var table = store.CreateTable();
        var entry = Entry();
        store.Seed(entry);
        var first = await table.ReadRowAsync(entry.SiloAddress, CancellationToken);
        store.BeforeTransaction = () =>
        {
            store.Seed(Entry());
            return Task.CompletedTask;
        };

        entry.Status = SiloStatus.ShuttingDown;
        var reads = store.Reads.Count;
        Assert.False(await table.UpdateRowAsync(entry, Assert.Single(first.Members).Item2, first.Version.Next(), CancellationToken));

        Assert.Equal(reads, store.Reads.Count);
        Assert.Equal(1, store.ConflictCount);
        Assert.Single(store.Transactions);
        var result = await table.ReadRowAsync(entry.SiloAddress, CancellationToken);
        var row = Assert.Single(result.Members);
        Assert.Equal(entry.IAmAliveTime, row.Item1.IAmAliveTime);
        Assert.Equal(SiloStatus.Active, row.Item1.Status);
        Assert.Equal(first.Version, result.Version);
    }

    private static MembershipEntry Entry(int id = 1) => new()
    {
        SiloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 10000 + id), 1),
        HostName = "host",
        SiloName = $"silo-{id}",
        Status = SiloStatus.Active,
        StartTime = Epoch,
        IAmAliveTime = Epoch.AddHours(1),
        SuspectTimes = new()
    };

    // Exercises the real SDK's serialization, HTTP status handling, and transaction results.
    // Hooks interleave independent providers immediately before the backend applies a transaction.
    private sealed class ConsulHandler : HttpMessageHandler
    {
        private Dictionary<string, KVPair> _rows = new(StringComparer.Ordinal);
        private readonly List<HttpClient> _httpClients = new();
        private readonly List<ConsulClient> _clients = new();
        private ulong _index;

        public ConsulClient Client => _clients[0];
        public IEnumerable<string> Keys => _rows.Keys;
        public int RequestCount { get; private set; }
        public int ConflictCount { get; private set; }
        public int ReturnedKeyCount { get; private set; }
        public List<(string Key, bool Recursive)> Reads { get; } = new();
        public List<List<KVTxnOp>> Transactions { get; } = new();
        public List<KVPair> Puts { get; } = new();
        public List<KVPair> CompareAndSets { get; } = new();
        public Func<Task>? BeforeTransaction { get; set; }
        public Func<CancellationToken, Task>? BeforePut { get; set; }
        public Func<string, bool, Task>? AfterRead { get; set; }
        public HttpMethod? FailureMethod { get; set; }
        public HttpStatusCode FailureStatus { get; set; } = HttpStatusCode.InternalServerError;
        public (int OpIndex, string What)[]? TransactionErrors { get; set; }
        public bool DeleteResult { get; set; } = true;
        public bool PutResult { get; set; } = true;
        public Func<List<KVPair>, List<KVPair>>? TransactionResults { get; set; }

        public ConsulBasedMembershipTable CreateTable(string cluster = "cluster", string? root = null)
        {
            var http = new HttpClient(this, disposeHandler: false);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            var client = new ConsulClient(new ConsulClientConfiguration { Address = new Uri("http://consul.invalid") }, http);
            _httpClients.Add(http);
            _clients.Add(client);
            var options = new ConsulClusteringOptions { KvRootFolder = root };
            options.ConfigureConsulClient(() => client);
            return new(NullLogger<ConsulBasedMembershipTable>.Instance, Options.Create(options),
                Options.Create(new ClusterOptions { ClusterId = cluster }));
        }

        public void Seed(MembershipEntry entry, bool heartbeat = true)
        {
            var key = $"orleans/cluster/{entry.SiloAddress.ToParsableString()}";
            Set(key, Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new
            {
                Hostname = entry.HostName,
                entry.ProxyPort,
                entry.StartTime,
                entry.Status,
                entry.SiloName,
                SuspectingSilos = entry.SuspectTimes?.Select(vote => new { Id = vote.Item1.ToParsableString(), Time = vote.Item2 })
            })));
            if (heartbeat)
            {
                Set(key + "/iamalive", Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(entry.IAmAliveTime)));
            }
        }

        public void Set(string key, byte[] value) => _rows[key] = new(key) { Value = value, ModifyIndex = ++_index };

        public string Snapshot(string? excludedKey = null) =>
            JsonConvert.SerializeObject(_rows.Values.Where(row => row.Key != excludedKey).OrderBy(row => row.Key, StringComparer.Ordinal));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(++RequestCount <= 200, "Consul request budget exhausted: possible unbounded retry.");
            if (request.Method == FailureMethod)
            {
                return Response(FailureStatus, "injected failure");
            }

            var uri = request.RequestUri!;
            if (uri.AbsolutePath == "/v1/txn" && request.Method == HttpMethod.Put)
            {
                var body = JArray.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var operations = body.Select(item => item["KV"]!.ToObject<KVTxnOp>()!).ToList();
                Transactions.Add(operations);
                if (TransactionErrors is { } errors)
                {
                    return Response(HttpStatusCode.Conflict, JsonConvert.SerializeObject(new
                    {
                        Errors = errors.Select(error => new { error.OpIndex, error.What })
                    }));
                }

                if (BeforeTransaction is { } before)
                {
                    BeforeTransaction = null;
                    await before();
                }

                cancellationToken.ThrowIfCancellationRequested();
                var next = new Dictionary<string, KVPair>(_rows, StringComparer.Ordinal);
                var index = ++_index;
                for (var i = 0; i < operations.Count; i++)
                {
                    var operation = operations[i];
                    next.TryGetValue(operation.Key, out var current);
                    var matches = operation.Verb.Equals(KVTxnVerb.DeleteCAS)
                        ? current is null || current.ModifyIndex == operation.Index
                        : operation.Verb.Equals(KVTxnVerb.CheckIndex)
                            ? current is not null && current.ModifyIndex == operation.Index
                            : (current?.ModifyIndex ?? 0) == operation.Index;
                    if (!matches)
                    {
                        ConflictCount++;
                        return Response(HttpStatusCode.Conflict, JsonConvert.SerializeObject(new
                        {
                            Errors = new[]
                            {
                                new
                                {
                                    OpIndex = i,
                                    What = $"failed to {(operation.Verb.Equals(KVTxnVerb.DeleteCAS) ? "delete" : "set")} key {JsonConvert.SerializeObject(operation.Key)}, index is stale"
                                }
                            }
                        }));
                    }

                    if (operation.Verb.Equals(KVTxnVerb.CAS))
                    {
                        next[operation.Key] = new(operation.Key) { Value = operation.Value, ModifyIndex = index };
                    }
                    else if (operation.Verb.Equals(KVTxnVerb.DeleteCAS))
                    {
                        next.Remove(operation.Key);
                    }
                    else
                    {
                        Assert.Equal(KVTxnVerb.CheckIndex, operation.Verb);
                    }
                }

                _rows = next;
                var results = operations.Where(operation => operation.Verb.Equals(KVTxnVerb.CAS))
                    .Select(operation => new KVPair(operation.Key) { ModifyIndex = next[operation.Key].ModifyIndex }).ToList();
                results = TransactionResults?.Invoke(results) ?? results;
                return Response(HttpStatusCode.OK, JsonConvert.SerializeObject(new
                {
                    Results = results.Select(result => new { KV = result })
                }));
            }

            Assert.StartsWith("/v1/kv/", uri.AbsolutePath);
            var key = Uri.UnescapeDataString(uri.AbsolutePath["/v1/kv/".Length..]);
            if (request.Method == HttpMethod.Put)
            {
                var value = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                if (uri.Query.StartsWith("?cas=", StringComparison.Ordinal))
                {
                    var index = ulong.Parse(uri.Query["?cas=".Length..], CultureInfo.InvariantCulture);
                    CompareAndSets.Add(new(key) { Value = value, ModifyIndex = index });
                    var matches = (_rows.GetValueOrDefault(key)?.ModifyIndex ?? 0) == index;
                    if (matches)
                    {
                        Set(key, value);
                    }

                    return Response(HttpStatusCode.OK, matches ? "true" : "false");
                }

                Assert.Empty(uri.Query);
                Puts.Add(new(key) { Value = value });
                if (BeforePut is { } before)
                {
                    await before(cancellationToken);
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (PutResult)
                {
                    Set(key, value);
                }

                return Response(HttpStatusCode.OK, PutResult ? "true" : "false");
            }

            if (request.Method == HttpMethod.Get)
            {
                Assert.Contains("consistent", uri.Query);
                var recursive = uri.Query.Contains("recurse", StringComparison.Ordinal);
                Reads.Add((key, recursive));
                var matches = _rows.Values.Where(row => recursive
                    ? row.Key.StartsWith(key, StringComparison.Ordinal)
                    : row.Key.Equals(key, StringComparison.Ordinal)).ToArray();
                ReturnedKeyCount += matches.Length;
                var response = matches.Length == 0
                    ? Response(HttpStatusCode.NotFound, string.Empty)
                    : Response(HttpStatusCode.OK, JsonConvert.SerializeObject(matches));
                if (AfterRead is { } after)
                {
                    await after(key, recursive);
                }

                return response;
            }

            Assert.Equal(HttpMethod.Delete, request.Method);
            Assert.Contains("recurse", uri.Query);
            if (DeleteResult)
            {
                foreach (var candidate in _rows.Keys.Where(candidate => candidate.StartsWith(key, StringComparison.Ordinal)).ToArray())
                {
                    _rows.Remove(candidate);
                }
            }

            return Response(HttpStatusCode.OK, DeleteResult ? "true" : "false");
        }

        private HttpResponseMessage Response(HttpStatusCode status, string body)
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            response.Headers.Add("X-Consul-Index", _index.ToString(CultureInfo.InvariantCulture));
            response.Headers.Add("X-Consul-Knownleader", "true");
            response.Headers.Add("X-Consul-Lastcontact", "0");
            return response;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var client in _clients)
                {
                    client.Dispose();
                }

                foreach (var http in _httpClients)
                {
                    http.Dispose();
                }
            }

            base.Dispose(disposing);
        }
    }
}
