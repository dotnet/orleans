using Orleans.Runtime;

namespace Orleans.Clustering.TestKit.Tests;

// Intentionally independent of the kit's snapshots, model transitions, and test-data copy routines.
internal sealed class IdealizedMembershipBackend
{
    internal readonly object Sync = new();
    internal readonly Dictionary<string, Partition> Partitions = new(StringComparer.Ordinal);
    internal long Tokens;
    internal bool ChangeHeartbeatEtag { get; init; }
    internal bool TableVersionRowEtags { get; init; }
    internal int CleanupBatchSize { get; init; } = int.MaxValue;
    internal bool VersionedCleanup { get; init; } = true;
    internal int CleanupBatches;
    internal bool RejectForeignClusterDeletion { get; init; }
    internal bool RequireInitializationAfterDeletion { get; init; }
    internal int ForeignClusterDeletionRejections;
    internal int CreatedHandles;
    internal int DisposedHandles;
    internal int Deletes;
    internal int VersionedUpdates;

    internal string Token() => $"opaque/{++Tokens:x}/token";
    internal sealed class Partition(string token)
    {
        internal int Version;
        internal string Etag = token;
        internal readonly Dictionary<SiloAddress, Tuple<MembershipEntry, string>> Rows = [];
    }

    internal IdealizedMembershipTable Create(string cluster)
    {
        Interlocked.Increment(ref CreatedHandles);
        return new(this, cluster);
    }

    internal MembershipTableTestFixture Fixture(string name = "Idealized")
        => new(name, (_, cluster, _) =>
        {
            var table = Create(cluster);
            return ValueTask.FromResult(new MembershipTableTestHandle(table, () =>
            {
                Interlocked.Increment(ref DisposedHandles);
                return ValueTask.CompletedTask;
            }));
        });
}

internal sealed class IdealizedMembershipTable(IdealizedMembershipBackend backend, string scopeClusterId) : IMembershipTable
{
    private IdealizedMembershipBackend.Partition Partition
    {
        get
        {
            if (!backend.Partitions.TryGetValue(scopeClusterId, out var value))
            {
                if (backend.RequireInitializationAfterDeletion)
                {
                    throw new InvalidOperationException("The membership table must be initialized before reading its history.");
                }

                backend.Partitions.Add(scopeClusterId, value = new(backend.Token()));
            }

            return value;
        }
    }

    public Task InitializeMembershipTableAsync(bool tryInitTableVersion, CancellationToken cancellationToken = default)
        => Locked(() =>
        {
            if (!backend.Partitions.ContainsKey(scopeClusterId))
            {
                backend.Partitions.Add(scopeClusterId, new(backend.Token()));
            }
        }, cancellationToken);

    public Task DeleteMembershipTableEntriesAsync(string clusterId, CancellationToken cancellationToken = default)
        => Locked(() =>
        {
            if (backend.RejectForeignClusterDeletion && !string.Equals(scopeClusterId, clusterId, StringComparison.Ordinal))
            {
                backend.ForeignClusterDeletionRejections++;
                throw new ArgumentException("The cluster is outside this provider's scope.", nameof(clusterId));
            }

            backend.Partitions.Remove(clusterId);
            backend.Deletes++;
        }, cancellationToken);

    public async Task CleanupDefunctSiloEntriesAsync(DateTimeOffset beforeDate, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var removed = await Locked(() =>
            {
                var partition = Partition;
                var rows = partition.Rows.Where(p => p.Value.Item1.Status == SiloStatus.Dead && EffectiveTime(p.Value.Item1) < beforeDate.UtcDateTime)
                    .Take(backend.CleanupBatchSize).ToArray();
                if (rows.Length == 0) return 0;
                var nextVersion = backend.VersionedCleanup ? checked(partition.Version + 1) : partition.Version;
                foreach (var row in rows) partition.Rows.Remove(row.Key);
                partition.Version = nextVersion;
                if (backend.VersionedCleanup) partition.Etag = backend.Token();
                backend.CleanupBatches++;
                return rows.Length;
            }, cancellationToken);
            if (removed == 0) return;
            await Task.Yield();
        }
    }

    public Task<MembershipTableData> ReadAllAsync(CancellationToken cancellationToken = default)
        => Locked(() => Snapshot(null), cancellationToken);

    public Task<MembershipTableData> ReadRowAsync(SiloAddress key, CancellationToken cancellationToken = default)
        => Locked(() => Snapshot(key), cancellationToken);

    public Task<bool> InsertRowAsync(MembershipEntry entry, TableVersion tableVersion, CancellationToken cancellationToken = default)
        => Locked(() =>
        {
            var partition = Partition;
            if (partition.Etag != tableVersion.VersionEtag || partition.Rows.ContainsKey(entry.SiloAddress)) return false;
            partition.Rows.Add(entry.SiloAddress, Tuple.Create(Clone(entry), backend.Token()));
            partition.Version = tableVersion.Version;
            partition.Etag = backend.Token();
            return true;
        }, cancellationToken);

    public Task<bool> UpdateRowAsync(MembershipEntry entry, string etag, TableVersion tableVersion, CancellationToken cancellationToken = default)
        => Locked(() =>
        {
            backend.VersionedUpdates++;
            var partition = Partition;
            if (partition.Etag != tableVersion.VersionEtag || !partition.Rows.TryGetValue(entry.SiloAddress, out var row)
                || (backend.TableVersionRowEtags ? partition.Etag : row.Item2) != etag) return false;
            var stored = Clone(entry);
            if (stored.IAmAliveTime < row.Item1.IAmAliveTime) stored.IAmAliveTime = row.Item1.IAmAliveTime;
            partition.Rows[stored.SiloAddress] = Tuple.Create(stored, backend.Token());
            partition.Version = tableVersion.Version;
            partition.Etag = backend.Token();
            return true;
        }, cancellationToken);

    public Task UpdateIAmAliveAsync(MembershipEntry entry, CancellationToken cancellationToken = default)
        => Locked(() =>
        {
            var partition = Partition;
            if (!partition.Rows.TryGetValue(entry.SiloAddress, out var row)) return;
            var stored = Clone(row.Item1);
            if (entry.IAmAliveTime > stored.IAmAliveTime) stored.IAmAliveTime = entry.IAmAliveTime;
            partition.Rows[stored.SiloAddress] = Tuple.Create(stored, backend.ChangeHeartbeatEtag ? backend.Token() : row.Item2);
        }, cancellationToken);

    internal static MembershipEntry Clone(MembershipEntry entry) => new()
    {
        SiloAddress = SiloAddress.New(entry.SiloAddress.Endpoint.Address, entry.SiloAddress.Endpoint.Port, entry.SiloAddress.Generation),
        Status = entry.Status, HostName = entry.HostName, SiloName = entry.SiloName, ProxyPort = entry.ProxyPort,
        RoleName = entry.RoleName, UpdateZone = entry.UpdateZone, FaultZone = entry.FaultZone,
        StartTime = entry.StartTime, IAmAliveTime = entry.IAmAliveTime,
        SuspectTimes = entry.SuspectTimes?.Select(t => Tuple.Create(
            SiloAddress.New(t.Item1.Endpoint.Address, t.Item1.Endpoint.Port, t.Item1.Generation), t.Item2)).ToList()
    };

    private MembershipTableData Snapshot(SiloAddress? key)
    {
        var partition = Partition;
        return new(partition.Rows.Where(p => key is null || p.Key.Equals(key))
            .Select(p => Tuple.Create(Clone(p.Value.Item1), backend.TableVersionRowEtags ? partition.Etag : p.Value.Item2)).ToList(),
            new(partition.Version, partition.Etag));
    }

    private static DateTime EffectiveTime(MembershipEntry entry)
    {
        var latest = entry.StartTime > entry.IAmAliveTime ? entry.StartTime : entry.IAmAliveTime;
        foreach (var vote in entry.SuspectTimes ?? [])
            if (vote.Item2 > latest) latest = vote.Item2;
        return latest;
    }

    private Task Locked(Action action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (backend.Sync) action();
        return Task.CompletedTask;
    }

    private Task<T> Locked<T>(Func<T> action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (backend.Sync) return Task.FromResult(action());
    }

    // The interface retains these members for compatibility. Tests call only the Async APIs above.
    public Task InitializeMembershipTable(bool tryInitTableVersion) => InitializeMembershipTableAsync(tryInitTableVersion);
    public Task DeleteMembershipTableEntries(string clusterId) => DeleteMembershipTableEntriesAsync(clusterId);
    public Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate) => CleanupDefunctSiloEntriesAsync(beforeDate);
    public Task<MembershipTableData> ReadAll() => ReadAllAsync();
    public Task<MembershipTableData> ReadRow(SiloAddress key) => ReadRowAsync(key);
    public Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion) => InsertRowAsync(entry, tableVersion);
    public Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion) => UpdateRowAsync(entry, etag, tableVersion);
    public Task UpdateIAmAlive(MembershipEntry entry) => UpdateIAmAliveAsync(entry);
}
