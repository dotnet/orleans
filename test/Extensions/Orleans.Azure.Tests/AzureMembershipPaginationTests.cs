using System.Net;
using System.Reflection;
using Azure;
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

    [Fact]
    public async Task OnePageReadUsesOneQuery()
    {
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(Query(false, Version(1, "v1"), Silo("silo-1", "s1")));

        var result = await CreateManager(storage).FindAllSiloEntries(TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.Equal(1, storage.QueryCount);
        Assert.Equal(2, storage.VersionReadCount);
    }

    [Fact]
    public async Task StablePaginatedReadUsesOneQuery()
    {
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(FencedQuery(1, Silo("silo-1", "s1")));

        var result = await CreateManager(storage).FindAllSiloEntries(TestContext.Current.CancellationToken);

        Assert.Equal(["silo-1", SiloInstanceTableEntry.TABLE_VERSION_ROW], result.Select(entry => entry.Entity.RowKey));
        Assert.Equal(1, storage.QueryCount);
        Assert.Equal(2, storage.VersionReadCount);
    }

    [Fact]
    public async Task TornPaginatedReadRetries()
    {
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(Query(
            true,
            BoundaryVersion(SiloInstanceTableEntry.TABLE_VERSION_ROW_MIN, 1, "before-1"),
            Silo("silo-1", "s1"),
            Version(2, "legacy-2"),
            BoundaryVersion(SiloInstanceTableEntry.TABLE_VERSION_ROW_MAX, 2, "after-2")),
            before: Version(1, "legacy-1"), after: Version(2, "legacy-2"));
        storage.AddQuery(FencedQuery(2, Silo("silo-2", "s2")));

        var result = await CreateManager(storage).FindAllSiloEntries(TestContext.Current.CancellationToken);

        Assert.Equal(["silo-2", SiloInstanceTableEntry.TABLE_VERSION_ROW], result.Select(entry => entry.Entity.RowKey));
        Assert.Equal(2, storage.QueryCount);
        Assert.Equal(4, storage.VersionReadCount);
    }

    [Fact]
    public async Task LegacyVersionAheadRetriesTornReadDuringRollingUpgrade()
    {
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(Query(
            true,
            BoundaryVersion(SiloInstanceTableEntry.TABLE_VERSION_ROW_MIN, 9, "before-9"),
            Silo("silo-1", "stale"),
            Version(11, "legacy-11"),
            BoundaryVersion(SiloInstanceTableEntry.TABLE_VERSION_ROW_MAX, 10, "after-10")),
            before: Version(10, "legacy-10"), after: Version(11, "legacy-11"));
        storage.AddQuery(Query(true, Silo("silo-1", "current"), Version(11, "legacy-11")));

        var result = await CreateManager(storage).FindAllSiloEntries(TestContext.Current.CancellationToken);

        Assert.Equal("current", result.Single(entry => entry.Entity.RowKey == "silo-1").ETag);
        Assert.Equal("legacy-11", result.Single(entry => entry.Entity.RowKey == SiloInstanceTableEntry.TABLE_VERSION_ROW).ETag);
        Assert.Equal(2, storage.QueryCount);
        Assert.Equal(4, storage.VersionReadCount);
    }

    [Fact]
    public async Task MultipleWritesDuringReadAreDetected()
    {
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(Query(
            true,
            BoundaryVersion(SiloInstanceTableEntry.TABLE_VERSION_ROW_MIN, 1, "before-1"),
            Silo("silo-1", "stale"),
            Version(3, "legacy-3"),
            BoundaryVersion(SiloInstanceTableEntry.TABLE_VERSION_ROW_MAX, 3, "after-3")),
            before: Version(1, "legacy-1"), after: Version(3, "legacy-3"));
        storage.AddQuery(FencedQuery(3, Silo("silo-1", "current")));

        var result = await CreateManager(storage).FindAllSiloEntries(TestContext.Current.CancellationToken);

        Assert.Equal("current", result.Single(entry => entry.Entity.RowKey == "silo-1").ETag);
        Assert.Equal(2, storage.QueryCount);
        Assert.Equal(4, storage.VersionReadCount);
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
                Version(attempt + 1, $"legacy-{attempt + 1}"),
                BoundaryVersion(SiloInstanceTableEntry.TABLE_VERSION_ROW_MAX, attempt + 1, $"after-{attempt + 1}")),
                before: Version(attempt, $"legacy-{attempt}"), after: Version(attempt + 1, $"legacy-{attempt + 1}"));
        }

        var exception = await Assert.ThrowsAsync<InconsistentStateException>(
            () => CreateManager(storage).FindAllSiloEntries(TestContext.Current.CancellationToken));

        Assert.Equal(
            $"Unable to read a consistent membership snapshot for cluster '{ClusterId}' from table '{TableName}' after {OrleansSiloInstanceManager.MaxMembershipSnapshotAttempts} attempts.",
            exception.Message);
        Assert.Equal(OrleansSiloInstanceManager.MaxMembershipSnapshotAttempts, storage.QueryCount);
        Assert.Equal(2 * OrleansSiloInstanceManager.MaxMembershipSnapshotAttempts, storage.VersionReadCount);
    }

    [Fact]
    public async Task MissingBoundaryRowsUseLegacyVersionFence()
    {
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(Query(true, Version(1, "v1"), Silo("silo-1", "s1")));

        var result = await CreateManager(storage).FindAllSiloEntries(TestContext.Current.CancellationToken);

        Assert.Equal("v1", result.Single(entry => entry.Entity.RowKey == SiloInstanceTableEntry.TABLE_VERSION_ROW).ETag);
        Assert.Equal(1, storage.QueryCount);
        Assert.Equal(2, storage.VersionReadCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VersionFenceRetriesChangedVersionWithoutBoundaryRows(bool paginated)
    {
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(Query(paginated, Silo("silo-1", "stale"), Version(2, "v2")),
            before: Version(1, "v1"), after: Version(2, "v2"));
        storage.AddQuery(Query(paginated, Silo("silo-1", "current"), Version(2, "v2")));

        var result = await CreateManager(storage).FindAllSiloEntries(TestContext.Current.CancellationToken);

        Assert.Equal("current", result.Single(entry => entry.Entity.RowKey == "silo-1").ETag);
        Assert.Equal("v2", result.Single(entry => entry.Entity.RowKey == SiloInstanceTableEntry.TABLE_VERSION_ROW).ETag);
        Assert.Equal(2, storage.QueryCount);
        Assert.Equal(4, storage.VersionReadCount);
    }

    [Fact]
    public async Task StableFenceWithDifferentQueriedVersionRetries()
    {
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(Query(true, Silo("silo-1", "stale"), Version(1, "v1")),
            before: Version(2, "v2"), after: Version(2, "v2"));
        storage.AddQuery(Query(true, Silo("silo-1", "current"), Version(2, "v2")));

        var result = await CreateManager(storage).FindAllSiloEntries(TestContext.Current.CancellationToken);

        Assert.Equal("current", result.Single(entry => entry.Entity.RowKey == "silo-1").ETag);
        Assert.Equal("v2", result.Single(entry => entry.Entity.RowKey == SiloInstanceTableEntry.TABLE_VERSION_ROW).ETag);
        Assert.Equal(2, storage.QueryCount);
        Assert.Equal(4, storage.VersionReadCount);
    }

    [Fact]
    public async Task MatchingBoundaryRowsDoNotHideConcurrentLegacyWrite()
    {
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(Query(
            true,
            BoundaryVersion(SiloInstanceTableEntry.TABLE_VERSION_ROW_MIN, 7, "before-7"),
            Silo("silo-1", "stale"),
            Version(9, "v9"),
            BoundaryVersion(SiloInstanceTableEntry.TABLE_VERSION_ROW_MAX, 7, "after-7")),
            before: Version(8, "v8"), after: Version(9, "v9"));
        storage.AddQuery(Query(true, Silo("silo-1", "current"), Silo("silo-2", "inserted"), Version(9, "v9")));

        var result = await CreateManager(storage).FindAllSiloEntries(TestContext.Current.CancellationToken);

        Assert.Equal(["silo-1", "silo-2", SiloInstanceTableEntry.TABLE_VERSION_ROW], result.Select(entry => entry.Entity.RowKey));
        Assert.Equal(["current", "inserted", "v9"], result.Select(entry => entry.ETag));
        Assert.Equal(2, storage.QueryCount);
        Assert.Equal(4, storage.VersionReadCount);
    }

    [Fact]
    public async Task VersionFenceAllowsDirtyIAmAliveEtagUpdate()
    {
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(FencedQuery(1, Silo("silo-1", "heartbeat-2")));

        var result = await CreateManager(storage).FindAllSiloEntries(TestContext.Current.CancellationToken);

        Assert.Equal("heartbeat-2", result.Single(entry => entry.Entity.RowKey == "silo-1").ETag);
        Assert.Equal(1, storage.QueryCount);
        Assert.Equal(2, storage.VersionReadCount);
    }

    [Fact]
    public async Task CancellationTokenFlowsThroughPaginatedRead()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(FencedQuery(1, Silo("silo-1", "s1")));

        await CreateManager(storage).FindAllSiloEntries(cancellation.Token);

        Assert.Equal(3, storage.CancellationTokens.Count);
        Assert.All(storage.CancellationTokens, token => Assert.Equal(cancellation.Token, token));
    }

    [Fact]
    public async Task CancellationBetweenQueryAndClosingFenceRejectsUnverifiedSnapshot()
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
        }, Version(1, "version-1"), Version(2, "version"));

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateManager(storage).FindAllSiloEntries(cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(1, storage.QueryCount);
        Assert.Equal(1, storage.VersionReadCount);
    }

    [Fact]
    public async Task CancellationStopsMembershipSnapshotRetries()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(FencedQuery(2), before: Version(1, "legacy-1"), after: Version(2, "legacy-2"));
        storage.OnVersionRead = count =>
        {
            if (count == 2) cancellation.Cancel();
        };

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateManager(storage).FindAllSiloEntries(cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(1, storage.QueryCount);
        Assert.Equal(2, storage.VersionReadCount);
    }

    [Fact]
    public async Task CompletedMembershipRead_PreservesSnapshotAfterCancellation()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(FencedQuery(2, Silo("silo-2", "s2")));
        storage.OnVersionRead = count =>
        {
            if (count == 2) cancellation.Cancel();
        };

        var result = await CreateManager(storage).FindAllSiloEntries(cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(["silo-2", SiloInstanceTableEntry.TABLE_VERSION_ROW], result.Select(entry => entry.Entity.RowKey));
        Assert.Equal(1, storage.QueryCount);
        Assert.Equal(2, storage.VersionReadCount);
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
        Assert.Equal(0, storage.VersionReadCount);
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
        Assert.Equal(["before-7", "legacy-7", "after-7"], versionRows.Select(row => row.ETag));
        Assert.Equal(1, storage.QueryCount);
        Assert.Equal(2, storage.VersionReadCount);
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
        Assert.Equal(4, storage.VersionReadCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HeartbeatAfterCompactionPreservesAbsenceAndVersion(bool deletionRacesMerge)
    {
        var client = CreateSiloClient(deletionRacesMerge ? StoredSilo() : null);
        var version = Version(7, "v7").Entity;
        _ = client.GetEntityAsync<SiloInstanceTableEntry>(
            ClusterId, SiloInstanceTableEntry.TABLE_VERSION_ROW, null, TestContext.Current.CancellationToken)
            .ReturnsForAnyArgs(Task.FromResult(Response.FromValue(version, Substitute.For<Response>())));
        _ = client.UpdateEntityAsync(
            Arg.Any<SiloInstanceTableEntry>(), Arg.Any<ETag>(), TableUpdateMode.Merge, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Response>(new RequestFailedException(404, "Retired silo.", "ResourceNotFound", null)));
        var table = CreateTable(CreateManager(null, client));

        await table.UpdateIAmAliveAsync(ProposedEntry(), TestContext.Current.CancellationToken);

        Assert.Equal("7", version.MembershipVersion);
        Assert.Equal(deletionRacesMerge ? 3 : 2, client.ReceivedCalls().Count());
        Assert.DoesNotContain(client.ReceivedCalls(), call => call.GetMethodInfo().Name is "AddEntityAsync" or "UpsertEntityAsync" or "SubmitTransactionAsync");
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
        Assert.Equal(["before-7", "legacy-7", "after-7"], versions.Select(row => row.ETag));
        Assert.Equal(1, storage.QueryCount);
        Assert.Equal(2, storage.VersionReadCount);
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
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task MembershipWriteUsesAtomicRowAndVersionConditionsAndMaximumHeartbeat(bool insert, bool newerHeartbeat)
    {
        var current = StoredSilo();
        current.IAmAliveTime = "2026-01-03 00:00:00.000 GMT";
        var client = CreateSiloClient(current);
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
        entry.IAmAliveTime = new DateTime(2026, 1, newerHeartbeat ? 4 : 2, 0, 0, 0, DateTimeKind.Utc);
        var proposedTime = entry.IAmAliveTime;
        var version = new TableVersion(7, "v7").Next();

        var result = insert
            ? await table.InsertRowAsync(entry, version, TestContext.Current.CancellationToken)
            : await table.UpdateRowAsync(entry, "s1", version, TestContext.Current.CancellationToken);

        Assert.True(result);
        Assert.NotNull(transaction);
        Assert.Equal(4, transaction.Length);
        Assert.Equal(insert ? TableTransactionActionType.Add : TableTransactionActionType.UpdateReplace, transaction[0].ActionType);
        Assert.Equal(current.RowKey, transaction[0].Entity.RowKey);
        if (!insert) Assert.Equal("s1", transaction[0].ETag.ToString());
        var written = Assert.IsType<SiloInstanceTableEntry>(transaction[0].Entity);
        Assert.Equal(LogFormatter.PrintDate(insert || newerHeartbeat ? proposedTime : LogFormatter.ParseDate(current.IAmAliveTime)), written.IAmAliveTime);
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
        Assert.Equal("2026-01-03 00:00:00.000 GMT", current.IAmAliveTime);
        Assert.Single(client.ReceivedCalls(), call => call.GetMethodInfo().Name == nameof(TableClient.SubmitTransactionAsync));
    }

    [Theory]
    [InlineData(true, 409, "EntityAlreadyExists")]
    [InlineData(true, 412, "UpdateConditionNotSatisfied")]
    [InlineData(false, 412, "UpdateConditionNotSatisfied")]
    [InlineData(false, 404, "ResourceNotFound")]
    public async Task ConditionalWriteConflictHasOneAtomicAttempt(bool insert, int status, string code)
    {
        var client = CreateSiloClient(StoredSilo());
        _ = client.SubmitTransactionAsync(Arg.Any<IEnumerable<TableTransactionAction>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Response<IReadOnlyList<Response>>>(new RequestFailedException(status, "Conflict.", code, null)));
        var table = CreateTable(CreateManager(null, client));
        var entry = ProposedEntry();
        var version = new TableVersion(7, "v7").Next();

        var result = insert
            ? await table.InsertRowAsync(entry, version, TestContext.Current.CancellationToken)
            : await table.UpdateRowAsync(entry, "s1", version, TestContext.Current.CancellationToken);

        Assert.False(result);
        var call = Assert.Single(client.ReceivedCalls(), call => call.GetMethodInfo().Name == nameof(TableClient.SubmitTransactionAsync));
        var transaction = Assert.IsAssignableFrom<IEnumerable<TableTransactionAction>>(call.GetArguments()[0]).ToArray();
        Assert.Equal(4, transaction.Length);
        Assert.Equal("v7", transaction[1].ETag.ToString());
        if (!insert) Assert.Equal("s1", transaction[0].ETag.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateRejectsMissingRowOrStaleRowEtagBeforeWriting(bool missing)
    {
        var current = StoredSilo();
        var client = CreateSiloClient(missing ? null : current);
        var table = CreateTable(CreateManager(null, client));

        Assert.False(await table.UpdateRowAsync(ProposedEntry(), "stale", new TableVersion(8, "v7"), TestContext.Current.CancellationToken));

        Assert.Equal(nameof(TableClient.GetEntityIfExistsAsync), Assert.Single(client.ReceivedCalls()).GetMethodInfo().Name);
        Assert.Equal("s1", current.ETag.ToString());
    }

    [Theory]
    [InlineData("Insert")]
    [InlineData("Update")]
    [InlineData("Heartbeat")]
    public async Task WriteTableNotFoundIsNotContention(string operation)
    {
        var client = CreateSiloClient(StoredSilo());
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
            "Update" => table.UpdateRowAsync(entry, "s1", new TableVersion(8, "v7"), TestContext.Current.CancellationToken),
            "Heartbeat" => table.UpdateIAmAliveAsync(entry, TestContext.Current.CancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        });

        Assert.Same(failure, exception);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task HeartbeatPreservesMaximumAndWritesOnlyLiveness(int offsetDays)
    {
        var current = StoredSilo();
        var client = CreateSiloClient(current);
        SiloInstanceTableEntry? written = null;
        _ = client.UpdateEntityAsync(Arg.Any<SiloInstanceTableEntry>(), Arg.Any<ETag>(), TableUpdateMode.Merge, Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                written = call.Arg<SiloInstanceTableEntry>();
                Assert.Equal("s1", call.Arg<ETag>().ToString());
                Assert.Equal(TestContext.Current.CancellationToken, call.Arg<CancellationToken>());
                return Substitute.ForPartsOf<Response>();
            });
        var table = CreateTable(CreateManager(null, client));
        var entry = ProposedEntry();
        entry.IAmAliveTime = LogFormatter.ParseDate(current.IAmAliveTime!).AddDays(offsetDays);

        await table.UpdateIAmAliveAsync(entry, TestContext.Current.CancellationToken);

        Assert.Equal("2026-01-01 00:00:00.000 GMT", current.IAmAliveTime);
        Assert.Equal(offsetDays > 0 ? 2 : 1, client.ReceivedCalls().Count());
        if (offsetDays > 0)
        {
            Assert.NotNull(written);
            Assert.Equal(current.RowKey, written.RowKey);
            Assert.Equal(LogFormatter.PrintDate(entry.IAmAliveTime), written.IAmAliveTime);
            Assert.Null(written.Status);
            Assert.Null(written.StartTime);
            Assert.Null(written.SuspectingSilos);
            Assert.Null(written.SuspectingTimes);
            Assert.Null(written.MembershipVersion);
        }
        else
        {
            Assert.Null(written);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HeartbeatConflictRereadsMaximumOrHonorsCancellation(bool cancel)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var current = StoredSilo();
        var client = CreateSiloClient(current);
        _ = client.UpdateEntityAsync(Arg.Any<SiloInstanceTableEntry>(), Arg.Any<ETag>(), TableUpdateMode.Merge, Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Assert.Equal("s1", call.Arg<ETag>().ToString());
                current.IAmAliveTime = "2026-01-03 00:00:00.000 GMT";
                current.ETag = new ETag("s2");
                if (cancel) cancellation.Cancel();
                return Task.FromException<Response>(new RequestFailedException(412, "Concurrent heartbeat."));
            });
        var table = CreateTable(CreateManager(null, client));

        if (cancel)
        {
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => table.UpdateIAmAliveAsync(ProposedEntry(), cancellation.Token));
            Assert.Equal(cancellation.Token, exception.CancellationToken);
        }
        else
        {
            await table.UpdateIAmAliveAsync(ProposedEntry(), cancellation.Token);
        }

        Assert.Equal("2026-01-03 00:00:00.000 GMT", current.IAmAliveTime);
        Assert.Equal(cancel ? 2 : 3, client.ReceivedCalls().Count());
        Assert.Single(client.ReceivedCalls(), call => call.GetMethodInfo().Name == nameof(TableClient.UpdateEntityAsync));
    }

    [Theory]
    [InlineData("Read", 404, null)]
    [InlineData("Read", 403, null)]
    [InlineData("Read", 503, null)]
    [InlineData("Read", 0, null)]
    [InlineData("Merge", 0, null)]
    [InlineData("History", 404, "ResourceNotFound")]
    [InlineData("History", 404, "TableNotFound")]
    [InlineData("History", 403, "AuthorizationFailure")]
    [InlineData("History", 503, "ServerBusy")]
    public async Task HeartbeatFailuresRemainVisible(string phase, int status, string? code)
    {
        var client = CreateSiloClient(phase == "History" ? null : StoredSilo());
        Exception failure = status == 0
            ? new System.Net.Http.HttpRequestException("Connection interrupted.")
            : new RequestFailedException(status, "Storage failure.", code, null);
        switch (phase)
        {
            case "Read":
                _ = client.GetEntityIfExistsAsync<SiloInstanceTableEntry>(string.Empty, string.Empty, null, TestContext.Current.CancellationToken)
                    .ReturnsForAnyArgs(Task.FromException<NullableResponse<SiloInstanceTableEntry>>(failure));
                break;
            case "Merge":
                _ = client.UpdateEntityAsync(Arg.Any<SiloInstanceTableEntry>(), Arg.Any<ETag>(), TableUpdateMode.Merge, Arg.Any<CancellationToken>())
                    .Returns(Task.FromException<Response>(failure));
                break;
            case "History":
                _ = client.GetEntityAsync<SiloInstanceTableEntry>(ClusterId, SiloInstanceTableEntry.TABLE_VERSION_ROW, null, TestContext.Current.CancellationToken)
                    .Returns(Task.FromException<Response<SiloInstanceTableEntry>>(failure));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(phase));
        }
        var table = CreateTable(CreateManager(null, client));

        var exception = await Record.ExceptionAsync(
            () => table.UpdateIAmAliveAsync(ProposedEntry(), TestContext.Current.CancellationToken));

        Assert.Same(failure, exception);
        Assert.Equal(phase == "Read" ? 1 : 2, client.ReceivedCalls().Count());
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
        Assert.Equal(new TableVersion(7, "legacy-7"), second.Version);
        current.Status = "malformed";
        storage.AddQuery(FencedQuery(7, (current, "s1")));
        await Assert.ThrowsAsync<ArgumentException>(() => table.ReadAllAsync(TestContext.Current.CancellationToken));
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
        var before = Version(7, "v7").Entity;
        var after = Version(8, "v8").Entity;
        _ = client.GetEntityAsync<SiloInstanceTableEntry>(ClusterId, SiloInstanceTableEntry.TABLE_VERSION_ROW, null, TestContext.Current.CancellationToken)
            .Returns(Response.FromValue(before, Substitute.For<Response>()),
                Response.FromValue(after, Substitute.For<Response>()),
                Response.FromValue(after, Substitute.For<Response>()),
                Response.FromValue(after, Substitute.For<Response>()));
        _ = client.QueryAsync<SiloInstanceTableEntry>(string.Empty, null, null, TestContext.Current.CancellationToken)
            .ReturnsForAnyArgs(Pages(stale, after), Pages(current, after));
        var table = CreateTable(CreateManager(null, client));

        var result = readRow
            ? await table.ReadRowAsync(ProposedEntry().SiloAddress, TestContext.Current.CancellationToken)
            : await table.ReadAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal("current", Assert.Single(result.Members).Item1.HostName);
        Assert.Equal(new TableVersion(8, "v8"), result.Version);
        Assert.Equal(
            ["GetEntityAsync", "QueryAsync", "GetEntityAsync", "GetEntityAsync", "QueryAsync", "GetEntityAsync"],
            client.ReceivedCalls().Select(call => call.GetMethodInfo().Name));

        static AsyncPageable<SiloInstanceTableEntry> Pages(SiloInstanceTableEntry row, SiloInstanceTableEntry version)
            => AsyncPageable<SiloInstanceTableEntry>.FromPages(
            [
                Page<SiloInstanceTableEntry>.FromValues([row], "next-page", Substitute.For<Response>()),
                Page<SiloInstanceTableEntry>.FromValues([version], null, Substitute.For<Response>())
            ]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeReadRequiresCanonicalHistory(bool readRow)
    {
        var client = CreateHeartbeatClient();
        var failure = new RequestFailedException(404, "Missing history.", "ResourceNotFound", null);
        _ = client.GetEntityAsync<SiloInstanceTableEntry>(ClusterId, SiloInstanceTableEntry.TABLE_VERSION_ROW, null, TestContext.Current.CancellationToken)
            .Returns(Task.FromException<Response<SiloInstanceTableEntry>>(failure));
        var table = CreateTable(CreateManager(null, client));

        var exception = await Assert.ThrowsAsync<RequestFailedException>(() => readRow
            ? table.ReadRowAsync(ProposedEntry().SiloAddress, TestContext.Current.CancellationToken)
            : table.ReadAllAsync(TestContext.Current.CancellationToken));

        Assert.Same(failure, exception);
        Assert.Equal(nameof(TableClient.GetEntityAsync), Assert.Single(client.ReceivedCalls()).GetMethodInfo().Name);
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

    private static TableClient CreateSiloClient(SiloInstanceTableEntry? current)
    {
        var client = CreateHeartbeatClient();
        _ = client.GetEntityIfExistsAsync<SiloInstanceTableEntry>(string.Empty, string.Empty, null, TestContext.Current.CancellationToken)
            .ReturnsForAnyArgs(current is null ? new MissingSiloResponse() : Response.FromValue(current, Substitute.For<Response>()));
        return client;
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
                Version(version, $"legacy-{version}"),
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

    private sealed class MissingSiloResponse : NullableResponse<SiloInstanceTableEntry>
    {
        public override bool HasValue => false;
        public override SiloInstanceTableEntry Value => throw new InvalidOperationException("The silo row is absent.");
        public override Response GetRawResponse() => Substitute.For<Response>();
    }

    private static TableClient CreateHeartbeatClient()
    {
        var client = Substitute.For<TableClient>();
        client.ReturnsForAll<Task<NullableResponse<SiloInstanceTableEntry>>>(
            Task.FromException<NullableResponse<SiloInstanceTableEntry>>(new InvalidOperationException("Unexpected silo read.")));
        client.ReturnsForAll<Task<Response<SiloInstanceTableEntry>>>(
            Task.FromException<Response<SiloInstanceTableEntry>>(new InvalidOperationException("Unexpected version read.")));
        return client;
    }

    private sealed class ScriptedMembershipTableReadStorage : IMembershipTableReadStorage
    {
        private readonly Queue<Func<MembershipTableQueryResult>> queries = new();
        private readonly Queue<(SiloInstanceTableEntry Entity, string ETag)> versions = new();
        private readonly Queue<string> operations = new();
        private readonly List<CancellationToken> cancellationTokens = new();

        public int QueryCount { get; private set; }
        public int VersionReadCount { get; private set; }
        public Action<int>? OnVersionRead { get; set; }

        public IReadOnlyList<CancellationToken> CancellationTokens => cancellationTokens;

        public void AddQuery(
            MembershipTableQueryResult result,
            (SiloInstanceTableEntry Entity, string ETag)? before = null,
            (SiloInstanceTableEntry Entity, string ETag)? after = null)
        {
            var version = result.Entries.Single(entry => entry.Entity.RowKey == SiloInstanceTableEntry.TABLE_VERSION_ROW);
            AddQuery(() => result, before ?? version, after ?? version);
        }

        public void AddQuery(
            Func<MembershipTableQueryResult> query,
            (SiloInstanceTableEntry Entity, string ETag) before,
            (SiloInstanceTableEntry Entity, string ETag) after)
        {
            operations.Enqueue("Version");
            operations.Enqueue("Query");
            operations.Enqueue("Version");
            queries.Enqueue(query);
            versions.Enqueue(before);
            versions.Enqueue(after);
        }

        public Task<(SiloInstanceTableEntry? Entity, string? ETag)> ReadTableVersionAsync(
            string partitionKey,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(ClusterId, partitionKey);
            Assert.Equal("Version", operations.Dequeue());
            cancellationTokens.Add(cancellationToken);
            VersionReadCount++;
            var result = versions.Dequeue();
            OnVersionRead?.Invoke(VersionReadCount);
            return Task.FromResult<(SiloInstanceTableEntry? Entity, string? ETag)>(result);
        }

        public Task<MembershipTableQueryResult> ReadAllTableEntriesForPartitionAsync(
            string partitionKey,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(ClusterId, partitionKey);
            Assert.Equal("Query", operations.Dequeue());
            cancellationTokens.Add(cancellationToken);
            QueryCount++;
            return Task.FromResult(queries.Dequeue()());
        }
    }
}
