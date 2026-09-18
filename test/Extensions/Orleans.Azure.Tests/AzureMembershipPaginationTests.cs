using System.Net;
using System.Net.Http;
using System.Reflection;
using Azure;
using Azure.Core.Pipeline;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.Extensions;
using Orleans.AzureUtils;
using Orleans.Clustering.AzureStorage;
using Orleans.Configuration;
using Orleans.Runtime.MembershipService;
using Orleans.Storage;
using Xunit;

namespace Tester.AzureUtils;

[TestCategory("AzureStorage"), TestCategory("Storage"), TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("AzureStorage")]
[TestArea("Persistence")]
public class AzureMembershipPaginationTests
{
    private const string ClusterId = "membership-pagination-tests";
    private const string TableName = "MembershipPaginationTests";

    [Theory]
    [InlineData("Initialize")]
    [InlineData("Delete")]
    [InlineData("Cleanup")]
    [InlineData("ReadRow")]
    [InlineData("ReadAll")]
    [InlineData("Insert")]
    [InlineData("Update")]
    [InlineData("Heartbeat")]
    public async Task CanceledOperationsDoNotAccessStorage(string operation)
    {
        var table = new AzureBasedMembershipTable(
            NullLoggerFactory.Instance,
            Options.Create(new AzureStorageClusteringOptions()),
            Options.Create(new ClusterOptions { ClusterId = ClusterId }));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        var token = cancellation.Token;
        var silo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11111), 1);
        var entry = new MembershipEntry { SiloAddress = silo };
        var version = new TableVersion(1, "etag");

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation switch
        {
            "Initialize" => table.InitializeMembershipTableAsync(true, token),
            "Delete" => table.DeleteMembershipTableEntriesAsync(ClusterId, token),
            "Cleanup" => table.CleanupDefunctSiloEntriesAsync(DateTimeOffset.MaxValue, token),
            "ReadRow" => table.ReadRowAsync(silo, token),
            "ReadAll" => table.ReadAllAsync(token),
            "Insert" => table.InsertRowAsync(entry, version, token),
            "Update" => table.UpdateRowAsync(entry, "etag", version, token),
            "Heartbeat" => table.UpdateIAmAliveAsync(entry, token),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        });

        Assert.Equal(token, exception.CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MatchingMarkersUseOneQuery(bool paginated)
    {
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(FencedQuery(1, Silo("silo-1", "s1")) with { IsPaginated = paginated });

        var result = await CreateManager(storage).FindAllSiloEntries(TestContext.Current.CancellationToken);

        Assert.Equal(["silo-1", SiloInstanceTableEntry.TABLE_VERSION_ROW], result.Select(entry => entry.Entity.RowKey));
        Assert.Equal(1, storage.QueryCount);
    }

    [Theory]
    [InlineData(1, 2, 2)]
    [InlineData(1, 3, 3)]
    [InlineData(7, 9, 7)]
    public async Task MismatchedMarkersRetry(int before, int version, int after)
    {
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(Query(
            true,
            BoundaryVersion(SiloInstanceTableEntry.TABLE_VERSION_ROW_MIN, before, "before"),
            Silo("silo-1", "stale"),
            Version(version, "version"),
            BoundaryVersion(SiloInstanceTableEntry.TABLE_VERSION_ROW_MAX, after, "after")));
        storage.AddQuery(FencedQuery(version, Silo("silo-1", "current")));

        var result = await CreateManager(storage).FindAllSiloEntries(TestContext.Current.CancellationToken);

        Assert.Equal("current", result.Single(entry => entry.Entity.RowKey == "silo-1").ETag);
        Assert.Equal(2, storage.QueryCount);
    }

    [Fact]
    public async Task PerpetualChurnFailsAfterBoundWithClusterContext()
    {
        var storage = new ScriptedMembershipTableReadStorage();
        for (var attempt = 0; attempt < OrleansSiloInstanceManager.MaxMembershipSnapshotAttempts; attempt++)
        {
            storage.AddQuery(Query(
                true,
                BoundaryVersion(SiloInstanceTableEntry.TABLE_VERSION_ROW_MIN, attempt, $"before-{attempt}"),
                Silo($"silo-{attempt}", $"s{attempt}"),
                Version(attempt + 1, $"version-{attempt + 1}"),
                BoundaryVersion(SiloInstanceTableEntry.TABLE_VERSION_ROW_MAX, attempt + 1, $"after-{attempt + 1}")));
        }

        var exception = await Assert.ThrowsAsync<InconsistentStateException>(
            () => CreateManager(storage).FindAllSiloEntries(TestContext.Current.CancellationToken));

        Assert.Equal(
            $"Unable to read a consistent membership snapshot for cluster '{ClusterId}' from table '{TableName}' after {OrleansSiloInstanceManager.MaxMembershipSnapshotAttempts} attempts.",
            exception.Message);
        Assert.Equal(OrleansSiloInstanceManager.MaxMembershipSnapshotAttempts, storage.QueryCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task MissingBoundaryRowsFail(bool opening, bool closing)
    {
        var storage = new ScriptedMembershipTableReadStorage();
        var query = FencedQuery(1, Silo("silo-1", "s1"));
        if (!opening) query.Entries.RemoveAt(0);
        if (!closing) query.Entries.RemoveAt(query.Entries.Count - 1);
        storage.AddQuery(query);

        var exception = await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateManager(storage).FindAllSiloEntries(TestContext.Current.CancellationToken));

        Assert.Equal("The membership query must include both ordered boundary version rows.", exception.Message);
        Assert.Equal(1, storage.QueryCount);
    }

    [Fact]
    public async Task VersionFenceAllowsDirtyIAmAliveEtagUpdate()
    {
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(FencedQuery(1, Silo("silo-1", "heartbeat-2")));

        var result = await CreateManager(storage).FindAllSiloEntries(TestContext.Current.CancellationToken);

        Assert.Equal("heartbeat-2", result.Single(entry => entry.Entity.RowKey == "silo-1").ETag);
        Assert.Equal(1, storage.QueryCount);
    }

    [Fact]
    public async Task CancellationTokenFlowsThroughPaginatedRead()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(FencedQuery(1, Silo("silo-1", "s1")));

        await CreateManager(storage).FindAllSiloEntries(cancellation.Token);

        Assert.Equal(cancellation.Token, Assert.Single(storage.CancellationTokens));
    }

    [Fact]
    public async Task CancellationStopsMembershipSnapshotRetries()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(() =>
        {
            cancellation.Cancel();
            return Query(
                true,
                BoundaryVersion(SiloInstanceTableEntry.TABLE_VERSION_ROW_MIN, 1, "before"),
                Version(2, "version"),
                BoundaryVersion(SiloInstanceTableEntry.TABLE_VERSION_ROW_MAX, 2, "after"));
        });

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateManager(storage).FindAllSiloEntries(cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(1, storage.QueryCount);
    }

    [Fact]
    public async Task CompletedMembershipRead_PreservesSnapshotAfterCancellation()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(() =>
        {
            cancellation.Cancel();
            return FencedQuery(2, Silo("silo-2", "s2"));
        });

        var result = await CreateManager(storage).FindAllSiloEntries(cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(["silo-2", SiloInstanceTableEntry.TABLE_VERSION_ROW], result.Select(entry => entry.Entity.RowKey));
        Assert.Equal(1, storage.QueryCount);
    }

    [Fact]
    public async Task CanceledMembershipReadDoesNotQueryStorage()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        var storage = new ScriptedMembershipTableReadStorage();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateManager(storage).FindAllSiloEntries(cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(0, storage.QueryCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(100)]
    public async Task CleanupUsesConditionalDeletionBatchesAndPreservesVersion(int maximumRows)
    {
        var storage = new ScriptedMembershipTableReadStorage();
        var rows = Enumerable.Range(0, 195).Select(index => DeadSilo($"silo-{index:D3}", $"s{index}")).ToArray();
        var snapshot = FencedQuery(7, rows);
        var versionRows = snapshot.Entries.Where(row => SiloInstanceTableEntry.IsVersionRow(row.Entity.RowKey)).ToArray();
        storage.AddQuery(snapshot);
        var batches = new List<TableTransactionAction[]>();
        var client = Substitute.For<TableClient>();
        _ = client.SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Assert.Equal(TestContext.Current.CancellationToken, call.Arg<CancellationToken>());
                batches.Add(call.Arg<IEnumerable<TableTransactionAction>>().ToArray());
                return Task.FromResult(Response.FromValue<IReadOnlyList<Response>>([], Substitute.For<Response>()));
            });

        await CreateManager(storage, client, maximumRows).CleanupDefunctSiloEntries(
            new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), TestContext.Current.CancellationToken);

        Assert.Equal(rows.Chunk(maximumRows).Select(chunk => chunk.Length), batches.Select(batch => batch.Length));
        var deletes = batches.SelectMany(batch => batch).ToArray();
        Assert.Equal(rows.Select(row => row.Entity.RowKey), deletes.Select(action => action.Entity.RowKey));
        Assert.Equal(rows.Select(row => row.ETag), deletes.Select(action => action.ETag.ToString()));
        Assert.All(deletes, action => Assert.Equal(TableTransactionActionType.Delete, action.ActionType));
        Assert.All(versionRows, row => Assert.Equal("7", row.Entity.MembershipVersion));
        Assert.Equal(["before-7", "version-7", "after-7"], versionRows.Select(row => row.ETag));
        Assert.Equal(1, storage.QueryCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupConflictReselectsHeartbeatAndVoteRecency(bool refreshVote)
    {
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(FencedQuery(0, DeadSilo("silo-0", "deleted"), DeadSilo("silo-1", "old")));
        var refreshed = DeadSilo("silo-1", "refreshed");
        if (refreshVote)
        {
            refreshed.Entity.SuspectingTimes = "2026-01-03 00:00:00.000 GMT";
        }
        else
        {
            refreshed.Entity.IAmAliveTime = "2026-01-03 00:00:00.000 GMT";
        }
        storage.AddQuery(FencedQuery(0, refreshed));
        var batches = new List<TableTransactionAction[]>();
        var client = Substitute.For<TableClient>();
        _ = client.SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var batch = call.Arg<IEnumerable<TableTransactionAction>>().ToArray();
                batches.Add(batch);
                return batch[0].Entity.RowKey == "silo-0"
                    ? Task.FromResult(SuccessfulTransaction())
                    : Task.FromException<Response<IReadOnlyList<Response>>>(new RequestFailedException(412, "Changed row etag."));
            });

        await CreateManager(storage, client, maximumRows: 1).CleanupDefunctSiloEntries(
            new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), TestContext.Current.CancellationToken);

        Assert.Equal(2, batches.Count);
        var deletions = batches.Select(batch => Assert.Single(batch)).ToArray();
        Assert.All(deletions, deletion => Assert.Equal(TableTransactionActionType.Delete, deletion.ActionType));
        Assert.Equal(["silo-0", "silo-1"], deletions.Select(deletion => deletion.Entity.RowKey));
        Assert.Equal(["deleted", "old"], deletions.Select(deletion => deletion.ETag.ToString()));
        Assert.Equal(2, storage.QueryCount);
    }

    [Theory]
    [InlineData(nameof(SiloInstanceTableEntry.StartTime))]
    [InlineData(nameof(SiloInstanceTableEntry.IAmAliveTime))]
    [InlineData(nameof(SiloInstanceTableEntry.SuspectingTimes))]
    public async Task CleanupUsesExclusiveTickPrecisionCutoff(string timestampField)
    {
        var cutoff = new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero);
        var row = DeadSilo("silo-1", "s1");
        const string timestamp = "2026-01-03 00:00:00.000 GMT";
        switch (timestampField)
        {
            case nameof(SiloInstanceTableEntry.StartTime): row.Entity.StartTime = timestamp; break;
            case nameof(SiloInstanceTableEntry.IAmAliveTime): row.Entity.IAmAliveTime = timestamp; break;
            case nameof(SiloInstanceTableEntry.SuspectingTimes): row.Entity.SuspectingTimes = timestamp; break;
        }
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(FencedQuery(0, row));
        storage.AddQuery(FencedQuery(0, row));
        var batches = new List<TableTransactionAction[]>();
        var client = Substitute.For<TableClient>();
        _ = client.SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                batches.Add(call.Arg<IEnumerable<TableTransactionAction>>().ToArray());
                return Task.FromResult(Response.FromValue<IReadOnlyList<Response>>([], Substitute.For<Response>()));
            });
        var manager = CreateManager(storage, client);

        await manager.CleanupDefunctSiloEntries(cutoff, TestContext.Current.CancellationToken);
        Assert.Empty(batches);
        await manager.CleanupDefunctSiloEntries(cutoff.AddTicks(1), TestContext.Current.CancellationToken);

        var batch = Assert.Single(batches);
        var deletion = Assert.Single(batch);
        Assert.Equal(TableTransactionActionType.Delete, deletion.ActionType);
        Assert.Equal("silo-1", deletion.Entity.RowKey);
        Assert.Equal("s1", deletion.ETag.ToString());
        Assert.Equal(2, storage.QueryCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupPreservesVersionWhenNoRowsAreEligible(bool populated)
    {
        var storage = new ScriptedMembershipTableReadStorage();
        var live = Enum.GetValues<SiloStatus>().Where(status => status != SiloStatus.Dead).Select(status =>
        {
            var row = DeadSilo(status.ToString(), status.ToString());
            row.Entity.Status = status.ToString();
            return row;
        }).ToArray();
        var suspected = DeadSilo("suspected", "suspected-etag");
        suspected.Entity.SuspectingTimes = "2026-01-03 00:00:00.000 GMT|2026-01-01 00:00:00.000 GMT";
        var snapshot = FencedQuery(7, populated ? [.. live, suspected] : []);
        var versions = snapshot.Entries.Where(row => SiloInstanceTableEntry.IsVersionRow(row.Entity.RowKey)).ToArray();
        storage.AddQuery(snapshot);
        var client = Substitute.For<TableClient>();

        await CreateManager(storage, client).CleanupDefunctSiloEntries(
            new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), TestContext.Current.CancellationToken);

        Assert.Empty(client.ReceivedCalls());
        Assert.All(versions, row => Assert.Equal("7", row.Entity.MembershipVersion));
        Assert.Equal(["before-7", "version-7", "after-7"], versions.Select(row => row.ETag));
        Assert.Equal(1, storage.QueryCount);
    }

    [Theory]
    [InlineData(404, "TableNotFound")]
    [InlineData(403, "AuthorizationFailure")]
    [InlineData(503, "ServerBusy")]
    public async Task CleanupInfrastructureFailuresRemainVisible(int status, string code)
    {
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(FencedQuery(7, DeadSilo("silo-1", "s1")));
        var client = Substitute.For<TableClient>();
        var failure = new RequestFailedException(status, "infrastructure-failure", code, null);
        _ = client.SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Response<IReadOnlyList<Response>>>(failure));

        var exception = await Assert.ThrowsAsync<RequestFailedException>(() => CreateManager(storage, client)
            .CleanupDefunctSiloEntries(DateTimeOffset.MaxValue, TestContext.Current.CancellationToken));

        Assert.Same(failure, exception);
        Assert.Equal(1, storage.QueryCount);
        Assert.Single(client.ReceivedCalls());
    }

    [Theory]
    [InlineData(false, 403, "AuthorizationFailure")]
    [InlineData(true, 403, "AuthorizationFailure")]
    [InlineData(false, 503, "ServerBusy")]
    [InlineData(true, 503, "ServerBusy")]
    public async Task CleanupPropagatesMixedBatchFailures(bool infrastructureFirst, int status, string code)
    {
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(FencedQuery(7, DeadSilo("silo-0", "s0"), DeadSilo("silo-1", "s1")));
        storage.AddQuery(FencedQuery(7));
        var client = Substitute.For<TableClient>();
        var first = new TaskCompletionSource<Response<IReadOnlyList<Response>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<Response<IReadOnlyList<Response>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var batchCount = 0;
        _ = client.SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Assert.Single(call.Arg<IEnumerable<TableTransactionAction>>());
                if (++batchCount == 1) return first.Task;
                started.SetResult();
                return second.Task;
            });
        var conflict = new RequestFailedException(412, "Concurrent heartbeat.");
        var infrastructure = new RequestFailedException(status, "Storage failure.", code, null);
        var cleanup = CreateManager(storage, client, maximumRows: 1)
            .CleanupDefunctSiloEntries(DateTimeOffset.MaxValue, TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        first.SetException(infrastructureFirst ? infrastructure : conflict);
        Assert.False(cleanup.IsCompleted);
        second.SetException(infrastructureFirst ? conflict : infrastructure);

        var exception = await Assert.ThrowsAsync<AggregateException>(() => cleanup);

        Assert.Equal(2, exception.InnerExceptions.Count);
        Assert.Contains(conflict, exception.InnerExceptions);
        Assert.Contains(infrastructure, exception.InnerExceptions);
        Assert.Equal(2, batchCount);
        Assert.Equal(1, storage.QueryCount);
    }

    [Fact]
    public async Task CleanupRetriesWhenEveryBatchFailureIsContention()
    {
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(FencedQuery(7, DeadSilo("silo-0", "s0"), DeadSilo("silo-1", "s1")));
        storage.AddQuery(FencedQuery(7));
        var client = Substitute.For<TableClient>();
        _ = client.SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromException<Response<IReadOnlyList<Response>>>(
                Assert.Single(call.Arg<IEnumerable<TableTransactionAction>>()).Entity.RowKey == "silo-0"
                    ? new RequestFailedException(412, "Concurrent heartbeat.")
                    : new RequestFailedException(404, "Concurrent cleanup.", "ResourceNotFound", null)));

        await CreateManager(storage, client, maximumRows: 1)
            .CleanupDefunctSiloEntries(DateTimeOffset.MaxValue, TestContext.Current.CancellationToken);

        Assert.Equal(2, client.ReceivedCalls().Count());
        Assert.Equal(2, storage.QueryCount);
    }

    [Fact]
    public async Task CleanupCancellationStopsConflictRetries()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(FencedQuery(7, DeadSilo("silo-1", "s1")));
        var client = Substitute.For<TableClient>();
        _ = client.SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), cancellation.Token)
            .Returns(_ =>
            {
                cancellation.Cancel();
                return Task.FromException<Response<IReadOnlyList<Response>>>(new RequestFailedException(412, "Concurrent heartbeat."));
            });

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateManager(storage, client)
            .CleanupDefunctSiloEntries(DateTimeOffset.MaxValue, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Single(client.ReceivedCalls());
        Assert.Equal(1, storage.QueryCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnerHeartbeatPreservesCanonicalTokensForMembershipUpdate(bool readRow)
    {
        var stored = StoredSilo();
        stored.Status = nameof(SiloStatus.Active);
        var version = Version(7, "v7").Entity;
        var client = CreateHeartbeatClient();
        var queryCount = 0;
        client.ReturnsForAll(AsyncPageable<SiloInstanceTableEntry>.FromPages([]));
        _ = client.QueryAsync<SiloInstanceTableEntry>(string.Empty, null, null, TestContext.Current.CancellationToken)
            .ReturnsForAnyArgs(_ => AsyncPageable<SiloInstanceTableEntry>.FromPages(Pages()));
        _ = client.UpdateEntityAsync(Arg.Any<SiloInstanceTableEntry>(), Arg.Any<ETag>(), TableUpdateMode.Merge, Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Assert.Equal(ETag.All, call.Arg<ETag>());
                stored.IAmAliveTime = call.Arg<SiloInstanceTableEntry>().IAmAliveTime;
                stored.ETag = new ETag("heartbeat-2");
                return Substitute.ForPartsOf<Response>();
            });
        TableTransactionAction[]? transaction = null;
        _ = client.SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                transaction = call.Arg<IEnumerable<TableTransactionAction>>().ToArray();
                return SuccessfulTransaction();
            });
        var table = CreateTable(CreateManager(null, client));
        var address = ProposedEntry().SiloAddress;
        var original = await Read();
        var row = Assert.Single(original.Members);
        Assert.Equal("v7", row.Item2);
        var heartbeat = new MembershipEntry
        {
            SiloAddress = address,
            IAmAliveTime = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc)
        };

        await table.UpdateIAmAliveAsync(heartbeat, TestContext.Current.CancellationToken);
        var refreshed = await Read();

        Assert.Equal(original.Version, refreshed.Version);
        Assert.Equal(row.Item2, Assert.Single(refreshed.Members).Item2);
        Assert.Equal(2, queryCount);
        var update = row.Item1;
        update.Status = SiloStatus.Dead;
        update.AddSuspector(address, heartbeat.IAmAliveTime);
        Assert.True(await table.UpdateRowAsync(update, row.Item2, original.Version.Next(), TestContext.Current.CancellationToken));

        Assert.NotNull(transaction);
        Assert.Equal(TableTransactionActionType.UpdateReplace, transaction[0].ActionType);
        Assert.Equal(ETag.All, transaction[0].ETag);
        Assert.Equal("v7", transaction[1].ETag.ToString());
        var written = Assert.IsType<SiloInstanceTableEntry>(transaction[0].Entity);
        Assert.Equal(nameof(SiloStatus.Dead), written.Status);
        Assert.Equal(address.ToParsableString(), written.SuspectingSilos);
        Assert.Equal(LogFormatter.PrintDate(heartbeat.IAmAliveTime), written.SuspectingTimes);
        Assert.Equal(LogFormatter.PrintDate(update.IAmAliveTime), written.IAmAliveTime);
        Assert.NotEqual(stored.IAmAliveTime, written.IAmAliveTime);
        Assert.Equal("8", Assert.IsType<SiloInstanceTableEntry>(transaction[1].Entity).MembershipVersion);
        Assert.Equal(["QueryAsync", "UpdateEntityAsync", "QueryAsync", "SubmitTransactionAsync"],
            client.ReceivedCalls().Select(call => call.GetMethodInfo().Name));

        Task<MembershipTableData> Read() => readRow
            ? table.ReadRowAsync(address, TestContext.Current.CancellationToken)
            : table.ReadAllAsync(TestContext.Current.CancellationToken);

        IEnumerable<Page<SiloInstanceTableEntry>> Pages()
        {
            queryCount++;
            yield return Page<SiloInstanceTableEntry>.FromValues(
                [BoundaryVersion(SiloInstanceTableEntry.TABLE_VERSION_ROW_MIN, 7, "before").Entity, stored, version],
                "next-page", Substitute.For<Response>());
            if (queryCount == 2) stored.ETag = new ETag("heartbeat-3");
            yield return Page<SiloInstanceTableEntry>.FromValues(
                [BoundaryVersion(SiloInstanceTableEntry.TABLE_VERSION_ROW_MAX, 7, "after").Entity],
                null, Substitute.For<Response>());
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MembershipWriteUsesAtomicCanonicalVersionCondition(bool insert)
    {
        var client = CreateHeartbeatClient();
        TableTransactionAction[]? transaction = null;
        _ = client.SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Assert.Equal(TestContext.Current.CancellationToken, call.Arg<CancellationToken>());
                transaction = call.Arg<IEnumerable<TableTransactionAction>>().ToArray();
                return SuccessfulTransaction();
            });
        var table = CreateTable(CreateManager(null, client));
        var entry = ProposedEntry();
        var proposedTime = entry.IAmAliveTime;
        var version = new TableVersion(7, "v7").Next();

        var result = insert
            ? await table.InsertRowAsync(entry, version, TestContext.Current.CancellationToken)
            : await table.UpdateRowAsync(entry, "v7", version, TestContext.Current.CancellationToken);

        Assert.True(result);
        Assert.NotNull(transaction);
        Assert.Equal(4, transaction.Length);
        Assert.Equal(insert ? TableTransactionActionType.Add : TableTransactionActionType.UpdateReplace, transaction[0].ActionType);
        Assert.Equal(SiloInstanceTableEntry.ConstructRowKey(entry.SiloAddress), transaction[0].Entity.RowKey);
        if (!insert) Assert.Equal(ETag.All, transaction[0].ETag);
        var written = Assert.IsType<SiloInstanceTableEntry>(transaction[0].Entity);
        Assert.Equal(LogFormatter.PrintDate(proposedTime), written.IAmAliveTime);
        Assert.Equal(nameof(SiloStatus.Active), written.Status);
        Assert.Equal(entry.HostName, written.HostName);
        var suspicion = Assert.Single(entry.SuspectTimes!);
        Assert.Equal(suspicion.Item1.ToParsableString(), written.SuspectingSilos);
        Assert.Equal(LogFormatter.PrintDate(suspicion.Item2), written.SuspectingTimes);
        Assert.Equal(TableTransactionActionType.UpdateReplace, transaction[1].ActionType);
        Assert.Equal(SiloInstanceTableEntry.TABLE_VERSION_ROW, transaction[1].Entity.RowKey);
        Assert.Equal("v7", transaction[1].ETag.ToString());
        Assert.Equal(
            [SiloInstanceTableEntry.TABLE_VERSION_ROW_MIN, SiloInstanceTableEntry.TABLE_VERSION_ROW_MAX],
            transaction.Skip(2).Select(action => action.Entity.RowKey));
        Assert.All(transaction.Skip(1), action =>
            Assert.Equal("8", Assert.IsType<SiloInstanceTableEntry>(action.Entity).MembershipVersion));
        Assert.All(transaction.Skip(2), action => Assert.Equal(TableTransactionActionType.UpsertReplace, action.ActionType));
        Assert.Equal(proposedTime, entry.IAmAliveTime);
        Assert.Equal(nameof(TableClient.SubmitTransactionAsync), Assert.Single(client.ReceivedCalls()).GetMethodInfo().Name);
    }

    [Theory]
    [InlineData(true, 409, "EntityAlreadyExists")]
    [InlineData(true, 412, "UpdateConditionNotSatisfied")]
    [InlineData(false, 412, "UpdateConditionNotSatisfied")]
    [InlineData(false, 404, "ResourceNotFound")]
    public async Task ConditionalWriteConflictHasOneAtomicAttempt(bool insert, int status, string code)
    {
        var client = CreateHeartbeatClient();
        _ = client.SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Response<IReadOnlyList<Response>>>(new RequestFailedException(status, "Conflict.", code, null)));
        var table = CreateTable(CreateManager(null, client));
        var entry = ProposedEntry();
        var version = new TableVersion(7, "v7").Next();

        var result = insert
            ? await table.InsertRowAsync(entry, version, TestContext.Current.CancellationToken)
            : await table.UpdateRowAsync(entry, "v7", version, TestContext.Current.CancellationToken);

        Assert.False(result);
        var call = Assert.Single(client.ReceivedCalls());
        Assert.Equal(nameof(TableClient.SubmitTransactionAsync), call.GetMethodInfo().Name);
        var transaction = Assert.IsAssignableFrom<IEnumerable<TableTransactionAction>>(call.GetArguments()[0]).ToArray();
        Assert.Equal(4, transaction.Length);
        Assert.Equal("v7", transaction[1].ETag.ToString());
        if (!insert) Assert.Equal(ETag.All, transaction[0].ETag);
    }

    [Fact]
    public async Task UpdateRejectsMismatchedCanonicalTokensBeforeWriting()
    {
        var client = CreateHeartbeatClient();
        var table = CreateTable(CreateManager(null, client));

        Assert.False(await table.UpdateRowAsync(ProposedEntry(), "stale", new TableVersion(8, "v7"), TestContext.Current.CancellationToken));

        Assert.Empty(client.ReceivedCalls());
    }

    [Theory]
    [InlineData("ResourceNotFound", false)]
    [InlineData("EntityNotFound", false)]
    [InlineData("TableNotFound", true)]
    public async Task UpdateDistinguishesMissingRowFromMissingTableThroughSdk(string errorCode, bool throws)
    {
        using var handler = new NotFoundTableHandler(errorCode);
        using var httpClient = new HttpClient(handler);
        var options = new TableClientOptions
        {
            Transport = new HttpClientTransport(httpClient),
            Retry = { MaxRetries = 0 }
        };
        var client = new TableClient(new Uri("https://unit.test"), TableName,
            new TableSharedKeyCredential("unitaccount", Convert.ToBase64String(new byte[32])), options);
        var table = CreateTable(CreateManager(null, client));
        var entry = ProposedEntry();
        var version = new TableVersion(8, "v7");

        if (throws)
        {
            var exception = await Assert.ThrowsAsync<RequestFailedException>(
                () => table.UpdateRowAsync(entry, "v7", version, TestContext.Current.CancellationToken));
            Assert.Equal(404, exception.Status);
            Assert.Equal(errorCode, exception.ErrorCode);
        }
        else
        {
            Assert.False(await table.UpdateRowAsync(entry, "v7", version, TestContext.Current.CancellationToken));
        }

        Assert.Equal(1, handler.RequestCount);
    }

    [Theory]
    [InlineData("Insert")]
    [InlineData("Update")]
    [InlineData("Heartbeat")]
    public async Task WriteTableNotFoundIsNotContention(string operation)
    {
        var client = CreateHeartbeatClient();
        var failure = new RequestFailedException(404, "Missing table.", "TableNotFound", null);
        _ = client.SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Response<IReadOnlyList<Response>>>(failure));
        _ = client.UpdateEntityAsync(Arg.Any<SiloInstanceTableEntry>(), Arg.Any<ETag>(), TableUpdateMode.Merge, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Response>(failure));
        var table = CreateTable(CreateManager(null, client));
        var entry = ProposedEntry();

        var exception = await Assert.ThrowsAsync<RequestFailedException>(() => operation switch
        {
            "Insert" => table.InsertRowAsync(entry, new TableVersion(8, "v7"), TestContext.Current.CancellationToken),
            "Update" => table.UpdateRowAsync(entry, "v7", new TableVersion(8, "v7"), TestContext.Current.CancellationToken),
            "Heartbeat" => table.UpdateIAmAliveAsync(entry, TestContext.Current.CancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        });

        Assert.Same(failure, exception);
    }

    [Fact]
    public async Task OwnerHeartbeatUsesSingleUnconditionalLivenessMerge()
    {
        var client = CreateHeartbeatClient();
        SiloInstanceTableEntry? written = null;
        _ = client.UpdateEntityAsync(Arg.Any<SiloInstanceTableEntry>(), Arg.Any<ETag>(), TableUpdateMode.Merge, Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                written = call.Arg<SiloInstanceTableEntry>();
                Assert.Equal(ETag.All, call.Arg<ETag>());
                Assert.Equal(TestContext.Current.CancellationToken, call.Arg<CancellationToken>());
                return Substitute.ForPartsOf<Response>();
            });
        var table = CreateTable(CreateManager(null, client));
        var entry = ProposedEntry();

        await table.UpdateIAmAliveAsync(entry, TestContext.Current.CancellationToken);

        Assert.Equal(nameof(TableClient.UpdateEntityAsync), Assert.Single(client.ReceivedCalls()).GetMethodInfo().Name);
        Assert.NotNull(written);
        Assert.Equal(ClusterId, written.PartitionKey);
        Assert.Equal(SiloInstanceTableEntry.ConstructRowKey(entry.SiloAddress), written.RowKey);
        Assert.Equal(LogFormatter.PrintDate(entry.IAmAliveTime), written.IAmAliveTime);
        Assert.Null(written.DeploymentId);
        Assert.Null(written.HostName);
        Assert.Null(written.Status);
        Assert.Null(written.StartTime);
        Assert.Null(written.SuspectingSilos);
        Assert.Null(written.SuspectingTimes);
        Assert.Null(written.MembershipVersion);
    }

    [Fact]
    public async Task HeartbeatMergeHonorsCancellation()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var client = CreateHeartbeatClient();
        _ = client.UpdateEntityAsync(Arg.Any<SiloInstanceTableEntry>(), Arg.Any<ETag>(), TableUpdateMode.Merge, Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Assert.Equal(cancellation.Token, call.Arg<CancellationToken>());
                cancellation.Cancel();
                return Task.FromCanceled<Response>(cancellation.Token);
            });
        var table = CreateTable(CreateManager(null, client));

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => table.UpdateIAmAliveAsync(ProposedEntry(), cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(nameof(TableClient.UpdateEntityAsync), Assert.Single(client.ReceivedCalls()).GetMethodInfo().Name);
    }

    [Theory]
    [InlineData(404, null)]
    [InlineData(404, "ResourceNotFound")]
    [InlineData(404, "EntityNotFound")]
    [InlineData(404, "TableNotFound")]
    [InlineData(403, "AuthorizationFailure")]
    [InlineData(503, "ServerBusy")]
    [InlineData(412, "UpdateConditionNotSatisfied")]
    [InlineData(0, null)]
    public async Task HeartbeatFailuresPropagateFromSingleMerge(int status, string? code)
    {
        var client = CreateHeartbeatClient();
        Exception failure = status == 0
            ? new System.Net.Http.HttpRequestException("Connection interrupted.")
            : new RequestFailedException(status, "Storage failure.", code, null);
        _ = client.UpdateEntityAsync(Arg.Any<SiloInstanceTableEntry>(), Arg.Any<ETag>(), TableUpdateMode.Merge, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Response>(failure));
        var table = CreateTable(CreateManager(null, client));

        var exception = await Record.ExceptionAsync(
            () => table.UpdateIAmAliveAsync(ProposedEntry(), TestContext.Current.CancellationToken));

        Assert.Same(failure, exception);
        Assert.Equal(nameof(TableClient.UpdateEntityAsync), Assert.Single(client.ReceivedCalls()).GetMethodInfo().Name);
    }

    [Fact]
    public async Task ReadReturnsDetachedMembershipAndRejectsMalformedRows()
    {
        var storage = new ScriptedMembershipTableReadStorage();
        var current = StoredSilo();
        var suspector = ProposedEntry().SuspectTimes!.Single().Item1;
        current.SuspectingSilos = suspector.ToParsableString();
        current.SuspectingTimes = "2026-01-01 00:00:00.000 GMT";
        storage.AddQuery(FencedQuery(7, (current, "s1")));
        storage.AddQuery(FencedQuery(7, (current, "s1")));
        var table = CreateTable(CreateManager(storage));

        var first = await table.ReadAllAsync(TestContext.Current.CancellationToken);
        var firstRow = Assert.Single(first.Members).Item1;
        firstRow.HostName = "changed";
        Assert.NotNull(firstRow.SuspectTimes);
        firstRow.SuspectTimes.Clear();
        var second = await table.ReadAllAsync(TestContext.Current.CancellationToken);
        var secondRow = Assert.Single(second.Members).Item1;

        Assert.NotSame(firstRow, secondRow);
        Assert.Equal("host", secondRow.HostName);
        Assert.NotNull(secondRow.SuspectTimes);
        Assert.Equal(suspector, Assert.Single(secondRow.SuspectTimes).Item1);
        Assert.Equal(new TableVersion(7, "version-7"), second.Version);
        current.Status = "malformed";
        storage.AddQuery(FencedQuery(7, (current, "s1")));
        await Assert.ThrowsAsync<ArgumentException>(() => table.ReadAllAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NativeSinglePageReadUsesOneQuery(bool readRow, bool present)
    {
        var client = CreateHeartbeatClient();
        client.ReturnsForAll(AsyncPageable<SiloInstanceTableEntry>.FromPages([]));
        var rows = FencedQuery(7, present ? [(StoredSilo(), "s1")] : []).Entries.Select(row => row.Entity).ToArray();
        _ = client.QueryAsync<SiloInstanceTableEntry>(string.Empty, null, null, TestContext.Current.CancellationToken)
            .ReturnsForAnyArgs(AsyncPageable<SiloInstanceTableEntry>.FromPages(
                [Page<SiloInstanceTableEntry>.FromValues(rows, null, Substitute.For<Response>())]));
        var table = CreateTable(CreateManager(null, client));
        var address = ProposedEntry().SiloAddress;

        var result = readRow
            ? await table.ReadRowAsync(address, TestContext.Current.CancellationToken)
            : await table.ReadAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new TableVersion(7, "version-7"), result.Version);
        if (present)
        {
            var member = Assert.Single(result.Members);
            Assert.Equal(address, member.Item1.SiloAddress);
            Assert.Equal(result.Version.VersionEtag, member.Item2);
        }
        else
        {
            Assert.Empty(result.Members);
        }

        var call = Assert.Single(client.ReceivedCalls());
        Assert.Equal(nameof(TableClient.QueryAsync), call.GetMethodInfo().Name);
        Assert.Equal(TestContext.Current.CancellationToken, call.GetArguments()[3]);
        if (readRow)
        {
            var filter = Assert.IsType<string>(call.GetArguments()[0]);
            Assert.Contains(SiloInstanceTableEntry.TABLE_VERSION_ROW_MIN, filter);
            Assert.Contains(SiloInstanceTableEntry.TABLE_VERSION_ROW_MAX, filter);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeReadCancellationBetweenPagesRejectsPartialSnapshot(bool readRow)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var client = CreateHeartbeatClient();
        client.ReturnsForAll(AsyncPageable<SiloInstanceTableEntry>.FromPages([]));
        _ = client.QueryAsync<SiloInstanceTableEntry>(string.Empty, null, null, cancellation.Token)
            .ReturnsForAnyArgs(AsyncPageable<SiloInstanceTableEntry>.FromPages(Pages()));
        var table = CreateTable(CreateManager(null, client));

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readRow
            ? table.ReadRowAsync(ProposedEntry().SiloAddress, cancellation.Token)
            : table.ReadAllAsync(cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(nameof(TableClient.QueryAsync), Assert.Single(client.ReceivedCalls()).GetMethodInfo().Name);

        IEnumerable<Page<SiloInstanceTableEntry>> Pages()
        {
            yield return Page<SiloInstanceTableEntry>.FromValues(
                [BoundaryVersion(SiloInstanceTableEntry.TABLE_VERSION_ROW_MIN, 7, "before").Entity, StoredSilo()],
                "next-page", Substitute.For<Response>());
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
        }
    }

    [Theory]
    [InlineData(SiloInstanceTableEntry.TABLE_VERSION_ROW_MIN)]
    [InlineData(SiloInstanceTableEntry.TABLE_VERSION_ROW)]
    [InlineData(SiloInstanceTableEntry.TABLE_VERSION_ROW_MAX)]
    public async Task NativeRowReadRequiresEveryVersionRow(string missingRow)
    {
        var query = FencedQuery(7, (StoredSilo(), "s1"));
        var rows = query.Entries.Where(row => row.Entity.RowKey != missingRow).Select(row => row.Entity).ToArray();
        var client = CreateHeartbeatClient();
        client.ReturnsForAll(AsyncPageable<SiloInstanceTableEntry>.FromPages([]));
        _ = client.QueryAsync<SiloInstanceTableEntry>(string.Empty, null, null, TestContext.Current.CancellationToken)
            .ReturnsForAnyArgs(AsyncPageable<SiloInstanceTableEntry>.FromPages(
                [Page<SiloInstanceTableEntry>.FromValues(rows, null, Substitute.For<Response>())]));
        var table = CreateTable(CreateManager(null, client));

        await Assert.ThrowsAsync<KeyNotFoundException>(() => table.ReadRowAsync(ProposedEntry().SiloAddress, TestContext.Current.CancellationToken));

        Assert.Equal(nameof(TableClient.QueryAsync), Assert.Single(client.ReceivedCalls()).GetMethodInfo().Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativePagedReadRetriesUntilRowAndCanonicalVersionAgree(bool readRow)
    {
        var client = CreateHeartbeatClient();
        client.ReturnsForAll(AsyncPageable<SiloInstanceTableEntry>.FromPages([]));
        var stale = StoredSilo();
        stale.HostName = "stale";
        var current = StoredSilo();
        current.HostName = "current";
        _ = client.QueryAsync<SiloInstanceTableEntry>(string.Empty, null, null, TestContext.Current.CancellationToken)
            .ReturnsForAnyArgs(Pages(stale, 7, 8), Pages(current, 8, 8));
        var table = CreateTable(CreateManager(null, client));

        var result = readRow
            ? await table.ReadRowAsync(ProposedEntry().SiloAddress, TestContext.Current.CancellationToken)
            : await table.ReadAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal("current", Assert.Single(result.Members).Item1.HostName);
        Assert.Equal(new TableVersion(8, "v8"), result.Version);
        Assert.Equal(["QueryAsync", "QueryAsync"], client.ReceivedCalls().Select(call => call.GetMethodInfo().Name));
        if (readRow)
        {
            Assert.All(client.ReceivedCalls(), call =>
            {
                var filter = Assert.IsType<string>(call.GetArguments()[0]);
                Assert.Contains(SiloInstanceTableEntry.TABLE_VERSION_ROW_MIN, filter);
                Assert.Contains(SiloInstanceTableEntry.TABLE_VERSION_ROW_MAX, filter);
            });
        }

        static AsyncPageable<SiloInstanceTableEntry> Pages(SiloInstanceTableEntry row, int before, int after)
            => AsyncPageable<SiloInstanceTableEntry>.FromPages(
            [
                Page<SiloInstanceTableEntry>.FromValues(
                    [BoundaryVersion(SiloInstanceTableEntry.TABLE_VERSION_ROW_MIN, before, "before").Entity, row],
                    "next-page", Substitute.For<Response>()),
                Page<SiloInstanceTableEntry>.FromValues(
                    [Version(after, $"v{after}").Entity, BoundaryVersion(SiloInstanceTableEntry.TABLE_VERSION_ROW_MAX, after, "after").Entity],
                    null, Substitute.For<Response>())
            ]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeReadPropagatesQueryFailure(bool readRow)
    {
        var client = CreateHeartbeatClient();
        client.ReturnsForAll(AsyncPageable<SiloInstanceTableEntry>.FromPages([]));
        var failure = new RequestFailedException(404, "Missing table.", "TableNotFound", null);
        _ = client.QueryAsync<SiloInstanceTableEntry>(string.Empty, null, null, TestContext.Current.CancellationToken)
            .ReturnsForAnyArgs(_ => throw failure);
        var table = CreateTable(CreateManager(null, client));

        var exception = await Assert.ThrowsAsync<OrleansException>(() => readRow
            ? table.ReadRowAsync(ProposedEntry().SiloAddress, TestContext.Current.CancellationToken)
            : table.ReadAllAsync(TestContext.Current.CancellationToken));

        Assert.Same(failure, exception.InnerException);
        Assert.Equal(nameof(TableClient.QueryAsync), Assert.Single(client.ReceivedCalls()).GetMethodInfo().Name);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    public void BoundaryVersionRowsSortAroundMembershipRows(string address)
    {
        var siloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Parse(address), 11111), 1);
        var rowKey = SiloInstanceTableEntry.ConstructRowKey(siloAddress);

        Assert.True(string.CompareOrdinal(SiloInstanceTableEntry.TABLE_VERSION_ROW_MIN, rowKey) < 0);
        Assert.True(string.CompareOrdinal(rowKey, SiloInstanceTableEntry.TABLE_VERSION_ROW_MAX) < 0);
    }

    private static OrleansSiloInstanceManager CreateManager(IMembershipTableReadStorage? storage, int maximumRows = 100)
        => new(
            ClusterId,
            NullLoggerFactory.Instance,
            new AzureStorageClusteringOptions
            {
                TableName = TableName,
                StoragePolicyOptions = { MaxBulkUpdateRows = maximumRows }
            },
            storage);

    private static OrleansSiloInstanceManager CreateManager(IMembershipTableReadStorage? storage, TableClient client, int maximumRows = 100)
    {
        var manager = CreateManager(storage, maximumRows);
        var dataManager = Assert.IsType<AzureTableDataManager<SiloInstanceTableEntry>>(
            typeof(OrleansSiloInstanceManager).GetField("storage", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager));
        typeof(AzureTableDataManager<SiloInstanceTableEntry>).GetProperty(nameof(AzureTableDataManager<SiloInstanceTableEntry>.Table))!
            .SetValue(dataManager, client);
        return manager;
    }

    private static AzureBasedMembershipTable CreateTable(OrleansSiloInstanceManager manager)
    {
        var table = new AzureBasedMembershipTable(
            NullLoggerFactory.Instance,
            Options.Create(new AzureStorageClusteringOptions { TableName = TableName }),
            Options.Create(new ClusterOptions { ClusterId = ClusterId }));
        typeof(AzureBasedMembershipTable).GetField("tableManager", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(table, manager);
        return table;
    }

    private static MembershipEntry ProposedEntry()
    {
        var entry = new MembershipEntry
        {
            SiloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11111), 1),
            HostName = "host",
            Status = SiloStatus.Active,
            StartTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            IAmAliveTime = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)
        };
        entry.AddSuspector(SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 22222), 2), entry.StartTime);
        return entry;
    }

    private static SiloInstanceTableEntry StoredSilo()
    {
        var row = DeadSilo(SiloInstanceTableEntry.ConstructRowKey(ProposedEntry().SiloAddress), "s1").Entity;
        row.Address = IPAddress.Loopback.ToString();
        row.Port = "11111";
        row.Generation = "1";
        row.HostName = "host";
        return row;
    }

    private static Response<IReadOnlyList<Response>> SuccessfulTransaction()
        => Response.FromValue<IReadOnlyList<Response>>(
            [Substitute.ForPartsOf<Response>(), Substitute.ForPartsOf<Response>(), Substitute.ForPartsOf<Response>(), Substitute.ForPartsOf<Response>()],
            Substitute.For<Response>());

    private static MembershipTableQueryResult FencedQuery(
        int version,
        params (SiloInstanceTableEntry Entity, string ETag)[] entries)
        => Query(
            true,
            [
                BoundaryVersion(SiloInstanceTableEntry.TABLE_VERSION_ROW_MIN, version, $"before-{version}"),
                .. entries,
                Version(version, $"version-{version}"),
                BoundaryVersion(SiloInstanceTableEntry.TABLE_VERSION_ROW_MAX, version, $"after-{version}")
            ]);

    private static MembershipTableQueryResult Query(
        bool isPaginated,
        params (SiloInstanceTableEntry Entity, string ETag)[] entries)
        => new([.. entries], isPaginated);

    private static (SiloInstanceTableEntry Entity, string ETag) Version(int version, string etag)
        => BoundaryVersion(SiloInstanceTableEntry.TABLE_VERSION_ROW, version, etag);

    private static (SiloInstanceTableEntry Entity, string ETag) BoundaryVersion(
        string rowKey,
        int version,
        string etag)
        => (new()
        {
            PartitionKey = ClusterId,
            RowKey = rowKey,
            DeploymentId = ClusterId,
            MembershipVersion = version.ToString(),
            ETag = new ETag(etag),
        }, etag);

    private static (SiloInstanceTableEntry Entity, string ETag) Silo(string rowKey, string etag)
        => (new()
        {
            PartitionKey = ClusterId,
            RowKey = rowKey,
            DeploymentId = ClusterId,
            ETag = new ETag(etag),
        }, etag);

    private static (SiloInstanceTableEntry Entity, string ETag) DeadSilo(string rowKey, string etag)
    {
        var row = Silo(rowKey, etag);
        row.Entity.Status = nameof(SiloStatus.Dead);
        row.Entity.StartTime = "2026-01-01 00:00:00.000 GMT";
        row.Entity.IAmAliveTime = row.Entity.StartTime;
        return row;
    }

    private sealed class NotFoundTableHandler(string errorCode) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/$batch", request.RequestUri!.AbsolutePath);
            var response = new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent(
                    $$$$"""{"odata.error":{"code":"{{{{errorCode}}}}","message":{"lang":"en-US","value":"Missing resource."}}}""",
                    System.Text.Encoding.UTF8, "application/json")
            };
            response.Headers.Add("x-ms-error-code", errorCode);
            return Task.FromResult(response);
        }
    }

    private static TableClient CreateHeartbeatClient()
    {
        var client = Substitute.For<TableClient>();
        client.ReturnsForAll<Task<Response<SiloInstanceTableEntry>>>(
            Task.FromException<Response<SiloInstanceTableEntry>>(new InvalidOperationException("Unexpected version read.")));
        return client;
    }

    private sealed class ScriptedMembershipTableReadStorage : IMembershipTableReadStorage
    {
        private readonly Queue<Func<MembershipTableQueryResult>> queries = new();
        private readonly List<CancellationToken> cancellationTokens = new();

        public int QueryCount { get; private set; }

        public IReadOnlyList<CancellationToken> CancellationTokens => cancellationTokens;

        public void AddQuery(MembershipTableQueryResult result) => AddQuery(() => result);

        public void AddQuery(Func<MembershipTableQueryResult> query) => queries.Enqueue(query);

        public Task<MembershipTableQueryResult> ReadAllTableEntriesForPartitionAsync(
            string partitionKey,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(ClusterId, partitionKey);
            cancellationTokens.Add(cancellationToken);
            QueryCount++;
            return Task.FromResult(queries.Dequeue()());
        }
    }
}
