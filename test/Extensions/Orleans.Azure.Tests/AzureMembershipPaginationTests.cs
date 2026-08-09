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
        Assert.Equal(0, storage.VersionReadCount);
    }

    [Fact]
    public async Task StablePaginationSucceeds()
    {
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(Query(true, Version(1, "v1"), Silo("silo-1", "s1")));
        storage.AddVersion(VersionRead(1, "v1"));
        storage.AddQuery(Query(true, Version(1, "v1"), Silo("silo-1", "s1")));
        storage.AddVersion(VersionRead(1, "v1"));

        var result = await CreateManager(storage).FindAllSiloEntries();

        Assert.Equal(["silo-1", SiloInstanceTableEntry.TABLE_VERSION_ROW], result.Select(entry => entry.Entity.RowKey));
        Assert.Equal(2, storage.QueryCount);
        Assert.Equal(2, storage.VersionReadCount);
    }

    [Fact]
    public async Task MutationBetweenPagesRetriesAndReturnsCoherentSnapshot()
    {
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(Query(true, Version(1, "v1"), Silo("silo-1", "s1")));

        storage.AddVersion(VersionRead(1, "v1"));
        storage.AddQuery(Query(true, Version(2, "v2"), Silo("silo-2", "s2")));
        storage.AddVersion(VersionRead(2, "v2"));

        storage.AddVersion(VersionRead(2, "v2"));
        storage.AddQuery(Query(true, Version(2, "v2"), Silo("silo-2", "s2")));
        storage.AddVersion(VersionRead(2, "v2"));

        var result = await CreateManager(storage).FindAllSiloEntries();

        Assert.Equal(["silo-2", SiloInstanceTableEntry.TABLE_VERSION_ROW], result.Select(entry => entry.Entity.RowKey));
        Assert.Equal("v2", result.Single(entry => entry.Entity.RowKey == SiloInstanceTableEntry.TABLE_VERSION_ROW).ETag);
        Assert.Equal(3, storage.QueryCount);
        Assert.Equal(4, storage.VersionReadCount);
    }

    [Fact]
    public async Task PerpetualChurnFailsAfterBoundWithVersionContext()
    {
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(Query(true, Version(0, "v0"), Silo("silo-0", "s0")));

        for (var attempt = 0; attempt < OrleansSiloInstanceManager.MaxMembershipSnapshotAttempts; attempt++)
        {
            storage.AddVersion(VersionRead(attempt, $"v{attempt}"));
            storage.AddQuery(Query(true, Version(attempt, $"v{attempt}"), Silo($"silo-{attempt}", $"s{attempt}")));
            storage.AddVersion(VersionRead(attempt + 1, $"v{attempt + 1}"));
        }

        var exception = await Assert.ThrowsAsync<InconsistentStateException>(
            () => CreateManager(storage).FindAllSiloEntries());

        Assert.Equal($"v{OrleansSiloInstanceManager.MaxMembershipSnapshotAttempts - 1}", exception.StoredEtag);
        Assert.Equal($"v{OrleansSiloInstanceManager.MaxMembershipSnapshotAttempts}", exception.CurrentEtag);
        Assert.Equal(OrleansSiloInstanceManager.MaxMembershipSnapshotAttempts + 1, storage.QueryCount);
        Assert.Equal(OrleansSiloInstanceManager.MaxMembershipSnapshotAttempts * 2, storage.VersionReadCount);
    }

    [Fact]
    public async Task VersionRowAbsencePresenceTransitionRetries()
    {
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(Query(true, Silo("silo-1", "s1")));

        storage.AddVersion((null, null));
        storage.AddQuery(Query(true, Version(1, "v1"), Silo("silo-1", "s1")));
        storage.AddVersion(VersionRead(1, "v1"));

        storage.AddVersion(VersionRead(1, "v1"));
        storage.AddQuery(Query(true, Version(1, "v1"), Silo("silo-1", "s1")));
        storage.AddVersion(VersionRead(1, "v1"));

        var result = await CreateManager(storage).FindAllSiloEntries();

        Assert.Equal("v1", result.Single(entry => entry.Entity.RowKey == SiloInstanceTableEntry.TABLE_VERSION_ROW).ETag);
        Assert.Equal(3, storage.QueryCount);
    }

    [Fact]
    public async Task PageEnumerationCompletesBeforeAfterFenceRead()
    {
        var events = new List<string>();
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(Query(true, Version(1, "v1"), Silo("silo-1", "s1")));
        storage.AddVersion(() =>
        {
            events.Add("before");
            return VersionRead(1, "v1");
        });
        storage.AddQuery(() =>
        {
            events.Add("page-1");
            events.Add("page-2");
            events.Add("query-complete");
            return Query(true, Version(1, "v1"), Silo("silo-1", "s1"));
        });
        storage.AddVersion(() =>
        {
            Assert.Equal("query-complete", events[^1]);
            events.Add("after");
            return VersionRead(1, "v1");
        });

        await CreateManager(storage).FindAllSiloEntries();

        Assert.Equal(["before", "page-1", "page-2", "query-complete", "after"], events);
    }

    [Fact]
    public async Task VersionFenceAllowsDirtyIAmAliveEtagUpdate()
    {
        var storage = new ScriptedMembershipTableReadStorage();
        storage.AddQuery(Query(true, Version(1, "v1"), Silo("silo-1", "heartbeat-1")));
        storage.AddVersion(VersionRead(1, "v1"));
        storage.AddQuery(Query(true, Version(1, "v1"), Silo("silo-1", "heartbeat-2")));
        storage.AddVersion(VersionRead(1, "v1"));

        var result = await CreateManager(storage).FindAllSiloEntries();

        Assert.Equal("heartbeat-2", result.Single(entry => entry.Entity.RowKey == "silo-1").ETag);
    }

    private static OrleansSiloInstanceManager CreateManager(IMembershipTableReadStorage storage)
        => new(
            ClusterId,
            NullLoggerFactory.Instance,
            new AzureStorageClusteringOptions { TableName = TableName },
            storage);

    private static MembershipTableQueryResult Query(
        bool isPaginated,
        params (SiloInstanceTableEntry Entity, string ETag)[] entries)
        => new([.. entries], isPaginated);

    private static (SiloInstanceTableEntry Entity, string ETag) Version(int version, string etag)
        => (new()
        {
            PartitionKey = ClusterId,
            RowKey = SiloInstanceTableEntry.TABLE_VERSION_ROW,
            DeploymentId = ClusterId,
            MembershipVersion = version.ToString(),
        }, etag);

    private static (SiloInstanceTableEntry? Entity, string? ETag) VersionRead(int version, string etag)
    {
        var result = Version(version, etag);
        return (result.Entity, result.ETag);
    }

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
        private readonly Queue<Func<(SiloInstanceTableEntry? Entity, string? ETag)>> versions = new();

        public int QueryCount { get; private set; }

        public int VersionReadCount { get; private set; }

        public void AddQuery(MembershipTableQueryResult result) => AddQuery(() => result);

        public void AddQuery(Func<MembershipTableQueryResult> query) => queries.Enqueue(query);

        public void AddVersion((SiloInstanceTableEntry? Entity, string? ETag) result) => AddVersion(() => result);

        public void AddVersion(Func<(SiloInstanceTableEntry? Entity, string? ETag)> version) => versions.Enqueue(version);

        public Task<MembershipTableQueryResult> ReadAllTableEntriesForPartitionAsync(string partitionKey)
        {
            Assert.Equal(ClusterId, partitionKey);
            QueryCount++;
            return Task.FromResult(queries.Dequeue()());
        }

        public Task<(SiloInstanceTableEntry? Entity, string? ETag)> ReadTableVersionAsync(string partitionKey, string rowKey)
        {
            Assert.Equal(ClusterId, partitionKey);
            Assert.Equal(SiloInstanceTableEntry.TABLE_VERSION_ROW, rowKey);
            VersionReadCount++;
            return Task.FromResult(versions.Dequeue()());
        }
    }
}
