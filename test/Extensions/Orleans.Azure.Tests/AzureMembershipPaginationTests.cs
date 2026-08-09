using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.AzureUtils;
using Orleans.Clustering.AzureStorage;
using Orleans.Storage;
using Xunit;

namespace Tester.AzureUtils;

[TestCategory("AzureStorage"), TestCategory("Storage")]
public class AzureMembershipPaginationTests
{
    private const string ClusterId = "membership-pagination-tests";
    private const string TableName = "MembershipPaginationTests";

    [Fact]
    public async Task OnePageReadUsesOneQuery()
    {
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(Query(false, Version(1, "v1"), Silo("silo-1", "s1")));

        var result = await CreateManager(storage).FindAllSiloEntries();

        Assert.Equal(2, result.Count);
        Assert.Equal(1, storage.QueryCount);
    }

    [Fact]
    public async Task StablePaginatedReadUsesOneQuery()
    {
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(FencedQuery(1, Silo("silo-1", "s1")));

        var result = await CreateManager(storage).FindAllSiloEntries();

        Assert.Equal(["silo-1", SiloInstanceTableEntry.TABLE_VERSION_ROW], result.Select(entry => entry.Entity.RowKey));
        Assert.Equal(1, storage.QueryCount);
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
            BoundaryVersion(SiloInstanceTableEntry.TABLE_VERSION_ROW_MAX, 2, "after-2")));
        storage.AddQuery(FencedQuery(2, Silo("silo-2", "s2")));

        var result = await CreateManager(storage).FindAllSiloEntries();

        Assert.Equal(["silo-2", SiloInstanceTableEntry.TABLE_VERSION_ROW], result.Select(entry => entry.Entity.RowKey));
        Assert.Equal(2, storage.QueryCount);
    }

    [Fact]
    public async Task LegacyVersionAheadAllowsTornReadDuringRollingUpgrade()
    {
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(Query(
            true,
            BoundaryVersion(SiloInstanceTableEntry.TABLE_VERSION_ROW_MIN, 9, "before-9"),
            Silo("silo-1", "stale"),
            Version(11, "legacy-11"),
            BoundaryVersion(SiloInstanceTableEntry.TABLE_VERSION_ROW_MAX, 10, "after-10")));

        var result = await CreateManager(storage).FindAllSiloEntries();

        Assert.Equal("stale", result.Single(entry => entry.Entity.RowKey == "silo-1").ETag);
        Assert.Equal(1, storage.QueryCount);
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
            BoundaryVersion(SiloInstanceTableEntry.TABLE_VERSION_ROW_MAX, 3, "after-3")));
        storage.AddQuery(FencedQuery(3, Silo("silo-1", "current")));

        var result = await CreateManager(storage).FindAllSiloEntries();

        Assert.Equal("current", result.Single(entry => entry.Entity.RowKey == "silo-1").ETag);
        Assert.Equal(2, storage.QueryCount);
    }

    [Fact]
    public async Task PerpetualChurnFailsAfterBoundWithVersionContext()
    {
        var storage = new ScriptedMembershipTableReadStorage();
        for (var attempt = 0; attempt < OrleansSiloInstanceManager.MaxMembershipSnapshotAttempts; attempt++)
        {
            storage.AddQuery(Query(
                true,
                BoundaryVersion(SiloInstanceTableEntry.TABLE_VERSION_ROW_MIN, attempt, $"before-{attempt}"),
                Silo($"silo-{attempt}", $"s{attempt}"),
                Version(attempt + 1, $"legacy-{attempt + 1}"),
                BoundaryVersion(SiloInstanceTableEntry.TABLE_VERSION_ROW_MAX, attempt + 1, $"after-{attempt + 1}")));
        }

        var exception = await Assert.ThrowsAsync<InconsistentStateException>(
            () => CreateManager(storage).FindAllSiloEntries());

        Assert.Equal((OrleansSiloInstanceManager.MaxMembershipSnapshotAttempts - 1).ToString(), exception.StoredEtag);
        Assert.Equal(OrleansSiloInstanceManager.MaxMembershipSnapshotAttempts.ToString(), exception.CurrentEtag);
        Assert.Equal(OrleansSiloInstanceManager.MaxMembershipSnapshotAttempts, storage.QueryCount);
    }

    [Fact]
    public async Task MissingBoundaryRowsSkipAtomicityCheck()
    {
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(Query(true, Version(1, "v1"), Silo("silo-1", "s1")));

        var result = await CreateManager(storage).FindAllSiloEntries();

        Assert.Equal("v1", result.Single(entry => entry.Entity.RowKey == SiloInstanceTableEntry.TABLE_VERSION_ROW).ETag);
        Assert.Equal(1, storage.QueryCount);
    }

    [Fact]
    public async Task VersionFenceAllowsDirtyIAmAliveEtagUpdate()
    {
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(FencedQuery(1, Silo("silo-1", "heartbeat-2")));

        var result = await CreateManager(storage).FindAllSiloEntries();

        Assert.Equal("heartbeat-2", result.Single(entry => entry.Entity.RowKey == "silo-1").ETag);
        Assert.Equal(1, storage.QueryCount);
    }

    [Fact]
    public async Task CancellationTokenFlowsThroughPaginatedRead()
    {
        using var cancellation = new CancellationTokenSource();
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(FencedQuery(1, Silo("silo-1", "s1")));

        await CreateManager(storage).FindAllSiloEntries(cancellation.Token);

        var token = Assert.Single(storage.CancellationTokens);
        Assert.Equal(cancellation.Token, token);
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

    private static OrleansSiloInstanceManager CreateManager(IMembershipTableReadStorage storage)
        => new(
            ClusterId,
            NullLoggerFactory.Instance,
            new AzureStorageClusteringOptions { TableName = TableName },
            storage);

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
        }, etag);

    private static (SiloInstanceTableEntry Entity, string ETag) Silo(string rowKey, string etag)
        => (new()
        {
            PartitionKey = ClusterId,
            RowKey = rowKey,
            DeploymentId = ClusterId,
        }, etag);

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
            Assert.Equal(ClusterId, partitionKey);
            cancellationTokens.Add(cancellationToken);
            QueryCount++;
            return Task.FromResult(queries.Dequeue()());
        }
    }
}
