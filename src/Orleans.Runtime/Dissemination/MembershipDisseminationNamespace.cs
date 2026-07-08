using System.Collections.Immutable;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.MembershipService;
using Orleans.Serialization;

namespace Orleans.Runtime.Dissemination;

internal sealed class MembershipDisseminationNamespace(
    IMembershipManager membershipManager,
    IOptionsMonitor<ClusterMembershipOptions> options,
    Serializer serializer) : IDisseminationNamespace
{
    private const int MaxSnapshotHistory = 32;
    private readonly object _historyLock = new();
    private readonly SortedDictionary<long, MembershipTableSnapshot> _snapshotHistory = new();

    public DisseminationNamespace Name => DisseminationNamespaceNames.Membership;

    public DisseminationGroup Group => DisseminationGroup.AllMembers;

    public DisseminationNamespaceOptions Options => options.CurrentValue.Dissemination;

    public DisseminationValue CreateValue(MembershipTableSnapshot snapshot)
    {
        RememberSnapshot(snapshot);
        return new DisseminationValue(
            DisseminationKey.Default,
            fromVersion: 0,
            toVersion: snapshot.Version.Value,
            serializer.SerializeToArray(new MembershipTableSnapshotUpdate { Snapshot = snapshot }));
    }

    public IReadOnlyDictionary<DisseminationKey, long> GetDigest()
    {
        var snapshot = membershipManager.CurrentSnapshot;
        RememberSnapshot(snapshot);
        return new Dictionary<DisseminationKey, long>
        {
            [DisseminationKey.Default] = snapshot.Version.Value,
        };
    }

    public long GetVersion(DisseminationKey key) =>
        key == DisseminationKey.Default
            ? membershipManager.CurrentSnapshot.Version.Value
            : 0;

    public bool TryCreateRepairValue(
        DisseminationKey key,
        long peerVersion,
        out DisseminationValue value)
    {
        if (key != DisseminationKey.Default)
        {
            value = default;
            return false;
        }

        var snapshot = membershipManager.CurrentSnapshot;
        RememberSnapshot(snapshot);
        if (snapshot.Version.Value <= peerVersion)
        {
            value = default;
            return false;
        }

        if (TryCreateDiffValue(peerVersion, snapshot, out value))
        {
            return true;
        }

        value = CreateValue(snapshot);
        return true;
    }

    public async ValueTask<DisseminationApplyResult> ApplyValueAsync(
        DisseminationValue value,
        CancellationToken cancellationToken)
    {
        if (value.Key != DisseminationKey.Default)
        {
            return DisseminationApplyResult.Rejected;
        }

        var update = serializer.Deserialize<MembershipTableSnapshotUpdate>(value.Payload);
        if (update.Diff is { } diff)
        {
            return update.Snapshot is null
                && value.FromVersion == diff.BaseVersion.Value
                && value.ToVersion == diff.Version.Value
                ? await ApplyDiff(diff, cancellationToken)
                : DisseminationApplyResult.Rejected;
        }

        if (update.Snapshot is not { } snapshot)
        {
            return DisseminationApplyResult.Rejected;
        }

        if (value.FromVersion != 0 || value.ToVersion != snapshot.Version.Value)
        {
            return DisseminationApplyResult.Rejected;
        }

        var currentVersion = membershipManager.CurrentSnapshot.Version;
        if (snapshot.Version < currentVersion)
        {
            return DisseminationApplyResult.Obsolete;
        }

        if (snapshot.Version == currentVersion)
        {
            return DisseminationApplyResult.Duplicate;
        }

        await membershipManager.ProcessGossipSnapshot(snapshot, cancellationToken);
        RememberSnapshot(snapshot);
        return DisseminationApplyResult.Applied;
    }

    private bool TryCreateDiffValue(long peerVersion, MembershipTableSnapshot snapshot, out DisseminationValue value)
    {
        MembershipTableSnapshot? baseSnapshot;
        lock (_historyLock)
        {
            _snapshotHistory.TryGetValue(peerVersion, out baseSnapshot);
        }

        if (baseSnapshot is null)
        {
            value = default!;
            return false;
        }
        var diff = CreateDiff(baseSnapshot, snapshot);
        value = new DisseminationValue(
            DisseminationKey.Default,
            fromVersion: peerVersion,
            toVersion: snapshot.Version.Value,
            serializer.SerializeToArray(new MembershipTableSnapshotUpdate { Diff = diff }));
        return true;
    }

    private async ValueTask<DisseminationApplyResult> ApplyDiff(MembershipTableSnapshotDiff diff, CancellationToken cancellationToken)
    {
        var current = membershipManager.CurrentSnapshot;
        if (current.Version.Value > diff.Version.Value)
        {
            return DisseminationApplyResult.Obsolete;
        }

        if (current.Version.Value == diff.Version.Value)
        {
            return DisseminationApplyResult.Duplicate;
        }

        if (current.Version.Value != diff.BaseVersion.Value)
        {
            return DisseminationApplyResult.Rejected;
        }

        var entries = current.Entries.ToBuilder();
        foreach (var silo in diff.RemovedSilos)
        {
            entries.Remove(silo);
        }

        foreach (var entry in diff.UpdatedEntries)
        {
            entries[entry.SiloAddress] = PreserveIAmAliveTime(current, entry);
        }

        var snapshot = new MembershipTableSnapshot(diff.Version, entries.ToImmutable());
        await membershipManager.ProcessGossipSnapshot(snapshot, cancellationToken);
        RememberSnapshot(snapshot);
        return DisseminationApplyResult.Applied;
    }

    private void RememberSnapshot(MembershipTableSnapshot snapshot)
    {
        lock (_historyLock)
        {
            _snapshotHistory[snapshot.Version.Value] = snapshot;
            while (_snapshotHistory.Count > MaxSnapshotHistory)
            {
                using var enumerator = _snapshotHistory.Keys.GetEnumerator();
                if (enumerator.MoveNext())
                {
                    _snapshotHistory.Remove(enumerator.Current);
                }
            }
        }
    }

    private static MembershipTableSnapshotDiff CreateDiff(MembershipTableSnapshot baseSnapshot, MembershipTableSnapshot snapshot)
    {
        var updated = ImmutableArray.CreateBuilder<MembershipEntry>();
        foreach (var entry in snapshot.Entries)
        {
            if (!baseSnapshot.Entries.TryGetValue(entry.Key, out var previous) || !MembershipEntriesEqual(previous, entry.Value))
            {
                updated.Add(entry.Value);
            }
        }

        var removed = ImmutableArray.CreateBuilder<SiloAddress>();
        foreach (var entry in baseSnapshot.Entries)
        {
            if (!snapshot.Entries.ContainsKey(entry.Key))
            {
                removed.Add(entry.Key);
            }
        }

        return new MembershipTableSnapshotDiff(
            baseSnapshot.Version,
            snapshot.Version,
            updated.ToImmutable(),
            removed.ToImmutable());
    }

    private static MembershipEntry PreserveIAmAliveTime(MembershipTableSnapshot previousSnapshot, MembershipEntry entry)
    {
        if (previousSnapshot.Entries.TryGetValue(entry.SiloAddress, out var previousEntry)
            && previousEntry.IAmAliveTime > entry.IAmAliveTime)
        {
            return CopyWithIAmAliveTime(entry, previousEntry.IAmAliveTime);
        }

        return entry;
    }

    private static MembershipEntry CopyWithIAmAliveTime(MembershipEntry entry, DateTime iAmAliveTime) => new()
    {
        SiloAddress = entry.SiloAddress,
        Status = entry.Status,
        SuspectTimes = entry.SuspectTimes is null ? null : new(entry.SuspectTimes),
        ProxyPort = entry.ProxyPort,
        HostName = entry.HostName,
        SiloName = entry.SiloName,
        RoleName = entry.RoleName,
        UpdateZone = entry.UpdateZone,
        FaultZone = entry.FaultZone,
        StartTime = entry.StartTime,
        IAmAliveTime = iAmAliveTime,
    };

    private static bool MembershipEntriesEqual(MembershipEntry left, MembershipEntry right) =>
        left.SiloAddress.Equals(right.SiloAddress)
        && left.Status == right.Status
        && EqualSuspectTimes(left.SuspectTimes, right.SuspectTimes)
        && left.ProxyPort == right.ProxyPort
        && string.Equals(left.HostName, right.HostName, StringComparison.Ordinal)
        && string.Equals(left.SiloName, right.SiloName, StringComparison.Ordinal)
        && string.Equals(left.RoleName, right.RoleName, StringComparison.Ordinal)
        && left.UpdateZone == right.UpdateZone
        && left.FaultZone == right.FaultZone
        && left.StartTime == right.StartTime
        && left.IAmAliveTime == right.IAmAliveTime;

    private static bool EqualSuspectTimes(List<Tuple<SiloAddress, DateTime>>? left, List<Tuple<SiloAddress, DateTime>>? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!Equals(left[i].Item1, right[i].Item1) || left[i].Item2 != right[i].Item2)
            {
                return false;
            }
        }

        return true;
    }
}

[GenerateSerializer, Immutable]
internal sealed class MembershipTableSnapshotUpdate
{
    [Id(0)]
    public MembershipTableSnapshot? Snapshot { get; init; }

    [Id(1)]
    public MembershipTableSnapshotDiff? Diff { get; init; }
}

[GenerateSerializer, Immutable]
internal sealed class MembershipTableSnapshotDiff
{
    public MembershipTableSnapshotDiff(
        MembershipVersion baseVersion,
        MembershipVersion version,
        ImmutableArray<MembershipEntry> updatedEntries,
        ImmutableArray<SiloAddress> removedSilos)
    {
        BaseVersion = baseVersion;
        Version = version;
        UpdatedEntries = updatedEntries;
        RemovedSilos = removedSilos;
    }

    [Id(0)]
    public MembershipVersion BaseVersion { get; }

    [Id(1)]
    public MembershipVersion Version { get; }

    [Id(2)]
    public ImmutableArray<MembershipEntry> UpdatedEntries { get; }

    [Id(3)]
    public ImmutableArray<SiloAddress> RemovedSilos { get; }
}
