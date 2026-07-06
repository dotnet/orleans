using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.Concurrency;
using Orleans.Configuration;
using Orleans.Runtime.MembershipService;
using Orleans.Serialization;

namespace Orleans.Runtime.Dissemination;

internal sealed class MembershipDisseminationTopic(
    IMembershipManager membershipManager,
    IOptionsMonitor<ClusterMembershipOptions> options,
    Serializer serializer,
    TimeProvider timeProvider,
    ILocalSiloDetails localSiloDetails) : IDisseminationTopic
{
    private const string MembershipKey = "cluster";
    private const int MaxSnapshotHistory = 32;
    private static readonly HashSet<string> SupportedPayloadKinds = new(StringComparer.Ordinal)
    {
        DisseminationTopicNames.MembershipSnapshot,
        DisseminationTopicNames.MembershipSnapshotDiff,
    };
    private readonly object _historyLock = new();
    private readonly SortedDictionary<long, MembershipTableSnapshot> _snapshotHistory = new();

    public string Name => DisseminationTopicNames.Membership;

    public DisseminationMembershipScope MembershipScope => DisseminationMembershipScope.AllMembers;

    public DisseminationTopicOptions Options => options.CurrentValue.Dissemination;

    public IReadOnlySet<string> PayloadKinds => SupportedPayloadKinds;

    public bool IsEnabled => Options.Enabled;

    public DisseminationValue CreateItem(SiloAddress origin, MembershipTableSnapshot snapshot)
    {
        RememberSnapshot(snapshot);
        var payload = serializer.SerializeToArray(snapshot);
        return new DisseminationValue
        {
            Digest = new DisseminationDigest(Name, MembershipKey, snapshot.Version.Value, DisseminationTopicNames.MembershipSnapshot),
            Root = origin,
            ExpiresAt = timeProvider.GetUtcNow() + Options.StaleItemTtl,
            Payload = payload,
        };
    }

    public IReadOnlyList<DisseminationDigest> GetDigests()
    {
        var snapshot = membershipManager.CurrentSnapshot;
        RememberSnapshot(snapshot);
        return new[]
        {
            new DisseminationDigest(Name, MembershipKey, snapshot.Version.Value, DisseminationTopicNames.MembershipSnapshot),
        };
    }

    public int CompareVersion(DisseminationDigest left, DisseminationDigest right) => left.Version.CompareTo(right.Version);

    public bool IsObsolete(DisseminationDigest digest) =>
        !IsSupportedPayloadKind(digest.PayloadKind)
        || digest.Key != MembershipKey
        || membershipManager.CurrentSnapshot.Version.Value > digest.Version;

    public ValueTask<DisseminationValue?> GetValue(
        DisseminationDigest digest,
        DisseminationDigest? peerDigest,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(digest.PayloadKind, DisseminationTopicNames.MembershipSnapshot, StringComparison.Ordinal)
            || digest.Key != MembershipKey)
        {
            return ValueTask.FromResult<DisseminationValue?>(null);
        }

        var snapshot = membershipManager.CurrentSnapshot;
        RememberSnapshot(snapshot);
        if (snapshot.Version.Value < digest.Version)
        {
            return ValueTask.FromResult<DisseminationValue?>(null);
        }

        if (peerDigest is { } remote
            && remote.Version < snapshot.Version.Value
            && TryCreateDiffValue(remote.Version, snapshot, out var diffValue))
        {
            return ValueTask.FromResult<DisseminationValue?>(diffValue);
        }

        return ValueTask.FromResult<DisseminationValue?>(CreateItem(localSiloDetails.SiloAddress, snapshot));
    }

    public async ValueTask<DisseminationApplyResult> ApplyValue(DisseminationValue value, CancellationToken cancellationToken)
    {
        if (value.Digest.Key != MembershipKey)
        {
            return DisseminationApplyResult.Rejected;
        }

        if (string.Equals(value.Digest.PayloadKind, DisseminationTopicNames.MembershipSnapshotDiff, StringComparison.Ordinal))
        {
            return await ApplyDiff(value, cancellationToken);
        }

        if (!string.Equals(value.Digest.PayloadKind, DisseminationTopicNames.MembershipSnapshot, StringComparison.Ordinal))
        {
            return DisseminationApplyResult.Rejected;
        }

        var snapshot = serializer.Deserialize<MembershipTableSnapshot>(value.Payload);
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

    public async ValueTask OnFallbackRequired(SiloAddress peer, DisseminationDigest digest, CancellationToken cancellationToken)
    {
        if (Options.FallbackEnabled)
        {
            await membershipManager.Refresh(new MembershipVersion(digest.Version), cancellationToken);
        }
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
        var payload = serializer.SerializeToArray(diff);
        value = new DisseminationValue
        {
            Digest = new DisseminationDigest(Name, MembershipKey, snapshot.Version.Value, DisseminationTopicNames.MembershipSnapshotDiff),
            Root = localSiloDetails.SiloAddress,
            ExpiresAt = timeProvider.GetUtcNow() + Options.StaleItemTtl,
            Payload = payload,
        };
        return true;
    }

    private async ValueTask<DisseminationApplyResult> ApplyDiff(DisseminationValue value, CancellationToken cancellationToken)
    {
        var diff = serializer.Deserialize<MembershipTableSnapshotDiff>(value.Payload);
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

    private static bool IsSupportedPayloadKind(string payloadKind) =>
        string.Equals(payloadKind, DisseminationTopicNames.MembershipSnapshot, StringComparison.Ordinal)
        || string.Equals(payloadKind, DisseminationTopicNames.MembershipSnapshotDiff, StringComparison.Ordinal);

    private static bool MembershipEntriesEqual(MembershipEntry left, MembershipEntry right) =>
        left.SiloAddress == right.SiloAddress
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
