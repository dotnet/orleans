using Orleans.Runtime;

namespace Orleans.Clustering.TestKit.Tests;

// Intentionally independent of the kit's snapshots, model transitions, and test-data copy routines.
internal sealed class IdealizedMembershipBackend
{
    internal readonly object Sync = new();
    internal readonly Dictionary<string, Partition> Partitions = new(StringComparer.Ordinal);
    internal long Tokens;
    internal bool PreserveHeartbeatOnFullWrite { get; init; }
    internal bool LagHeartbeatReads { get; init; }
    internal int LaggedHeartbeatReads;
    internal bool TableVersionRowEtags { get; init; }
    internal int CleanupBatchSize { get; init; } = int.MaxValue;
    internal bool VersionedCleanup { get; init; } = true;
    internal int CleanupBatches;
    internal bool RejectForeignClusterDeletion { get; init; }
    internal bool TerminalDeletion { get; init; }
    internal bool DestructiveDisposal { get; init; }
    internal readonly HashSet<string> DeletedScopes = new(StringComparer.Ordinal);
    internal int OperationsAfterDeletion;
    internal int DeletionProbes;
    internal int ForeignClusterDeletionRejections;
    internal int CreatedHandles;
    internal int DisposedHandles;
    internal int Deletes;
    internal int VersionedUpdates;
    internal int Reads;
    internal readonly List<(string Cluster, IdealizedMembershipTable Owner, SiloAddress Identity, DateTime Time, SiloStatus Status)> HeartbeatWrites = [];

    internal string Token() => $"opaque/{++Tokens:x}/token";
    internal sealed class Partition(string token)
    {
        internal int Version;
        internal string Etag = token;
        internal readonly Dictionary<SiloAddress, Tuple<MembershipEntry, string>> Rows = [];
        internal readonly Dictionary<SiloAddress, DateTime> EarlierHeartbeats = [];
    }

    internal IdealizedMembershipTable Create(string cluster)
    {
        lock (Sync) AssertUsable(cluster);
        Interlocked.Increment(ref CreatedHandles);
        return new(this, cluster);
    }

    internal void AssertUsable(string cluster)
    {
        if (DeletedScopes.Contains(cluster))
        {
            OperationsAfterDeletion++;
            throw new InvalidOperationException("The provider owner was terminally invalidated.");
        }
    }

    internal ValueTask<bool> IsDeletedAsync(string cluster, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (Sync)
        {
            DeletionProbes++;
            return ValueTask.FromResult(TerminalDeletion
                ? DeletedScopes.Contains(cluster)
                : !Partitions.TryGetValue(cluster, out var partition) || partition.Rows.Count == 0);
        }
    }

    internal ValueTask DisposeHandleAsync(string cluster)
    {
        lock (Sync)
        {
            DisposedHandles++;
            if (DestructiveDisposal) Partitions.Remove(cluster);
        }
        return ValueTask.CompletedTask;
    }

    internal MembershipTableTestFixture Fixture(string name = "Idealized")
        => new(name, (_, cluster, _) =>
        {
            var table = Create(cluster);
            return ValueTask.FromResult(new MembershipTableTestHandle(table, () => DisposeHandleAsync(cluster)));
        }, IsDeletedAsync);
}

internal sealed class IdealizedMembershipTable(IdealizedMembershipBackend backend, string scopeClusterId) : IMembershipTable
{
    internal int InitializeCalls { get; private set; }

    private IdealizedMembershipBackend.Partition Partition
    {
        get
        {
            if (!backend.Partitions.TryGetValue(scopeClusterId, out var value))
            {
                backend.Partitions.Add(scopeClusterId, value = new(backend.Token()));
            }

            return value;
        }
    }

    public Task InitializeMembershipTableAsync(bool tryInitTableVersion, CancellationToken cancellationToken = default)
        => Locked(() =>
        {
            InitializeCalls++;
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
            if (backend.TerminalDeletion) backend.DeletedScopes.Add(clusterId);
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
            if (backend.PreserveHeartbeatOnFullWrite && stored.IAmAliveTime < row.Item1.IAmAliveTime)
                stored.IAmAliveTime = row.Item1.IAmAliveTime;
            partition.Rows[stored.SiloAddress] = Tuple.Create(stored, backend.Token());
            partition.Version = tableVersion.Version;
            partition.Etag = backend.Token();
            return true;
        }, cancellationToken);

    public Task UpdateIAmAliveAsync(MembershipEntry entry, CancellationToken cancellationToken = default)
        => Locked(() =>
        {
            var partition = Partition;
            var row = partition.Rows[entry.SiloAddress];
            backend.HeartbeatWrites.Add((scopeClusterId, this, entry.SiloAddress, entry.IAmAliveTime, row.Item1.Status));
            partition.EarlierHeartbeats.TryAdd(entry.SiloAddress, row.Item1.IAmAliveTime);
            row.Item1.IAmAliveTime = entry.IAmAliveTime;
        }, cancellationToken);

    internal static MembershipEntry Clone(MembershipEntry entry) => new()
    {
        SiloAddress = SiloAddress.New(entry.SiloAddress.Endpoint.Address, entry.SiloAddress.Endpoint.Port, entry.SiloAddress.Generation),
        Status = entry.Status,
        HostName = entry.HostName,
        SiloName = entry.SiloName,
        ProxyPort = entry.ProxyPort,
        RoleName = entry.RoleName,
        UpdateZone = entry.UpdateZone,
        FaultZone = entry.FaultZone,
        StartTime = entry.StartTime,
        IAmAliveTime = entry.IAmAliveTime,
        SuspectTimes = entry.SuspectTimes?.Select(t => Tuple.Create(
            SiloAddress.New(t.Item1.Endpoint.Address, t.Item1.Endpoint.Port, t.Item1.Generation), t.Item2)).ToList()
    };

    private MembershipTableData Snapshot(SiloAddress? key)
    {
        backend.Reads++;
        var partition = Partition;
        return new(partition.Rows.Where(p => key is null || p.Key.Equals(key))
            .Select(p =>
            {
                var entry = Clone(p.Value.Item1);
                if (backend.LagHeartbeatReads && backend.Reads % 2 == 0 && partition.EarlierHeartbeats.TryGetValue(p.Key, out var earlier))
                {
                    entry.IAmAliveTime = earlier;
                    backend.LaggedHeartbeatReads++;
                }
                return Tuple.Create(entry, backend.TableVersionRowEtags ? partition.Etag : p.Value.Item2);
            }).ToList(),
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
        lock (backend.Sync)
        {
            backend.AssertUsable(scopeClusterId);
            action();
        }
        return Task.CompletedTask;
    }

    private Task<T> Locked<T>(Func<T> action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (backend.Sync)
        {
            backend.AssertUsable(scopeClusterId);
            return Task.FromResult(action());
        }
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
