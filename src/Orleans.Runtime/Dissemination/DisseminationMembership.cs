using System.Collections.Immutable;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.MembershipService;

namespace Orleans.Runtime.Dissemination;

internal sealed class DisseminationMembership(
    IMembershipManager membershipManager,
    ILocalSiloDetails localSiloDetails,
    IOptions<DisseminationOptions> options)
{
    private readonly object _membershipLock = new();
    private CachedMembership? _current;

    public DisseminationMembershipSnapshots CurrentSnapshots
    {
        get
        {
            var membershipSnapshot = membershipManager.CurrentSnapshot;
            var current = Volatile.Read(ref _current);
            if (current is not null && ReferenceEquals(current.Source, membershipSnapshot))
            {
                return current.Snapshots;
            }

            lock (_membershipLock)
            {
                current = Volatile.Read(ref _current);
                if (current is not null && !membershipSnapshot.IsSuccessorTo(current.Source))
                {
                    return current.Snapshots;
                }

                // Same-version heartbeats and removal of non-participants retain the existing topology.
                var snapshots = current is { } previous && HasSameTopology(previous, membershipSnapshot)
                    ? previous.Snapshots
                    : ComputeMembership(membershipSnapshot, localSiloDetails.SiloAddress, options.Value.Overlay, current?.Snapshots);
                Volatile.Write(ref _current, new(membershipSnapshot, snapshots));
                return snapshots;
            }
        }
    }

    public DisseminationMembershipSnapshot CurrentSnapshot => CurrentSnapshots.AllMembers;

    public DisseminationMembershipSnapshot GetSnapshot(DisseminationMembershipScope scope) =>
        CurrentSnapshots.GetSnapshot(scope);

    public Task RefreshMembership(CancellationToken cancellationToken) =>
        membershipManager.Refresh(targetVersion: null, cancellationToken: cancellationToken);

    public async ValueTask<DisseminationMembershipSnapshots?> GetSnapshotsContainingMember(
        SiloAddress member,
        DisseminationMembershipScope scope,
        CancellationToken cancellationToken)
    {
        var snapshots = CurrentSnapshots;
        if (snapshots.GetSnapshot(scope).ContainsMember(member))
        {
            return snapshots;
        }

        await RefreshMembership(cancellationToken);
        snapshots = CurrentSnapshots;
        return snapshots.GetSnapshot(scope).ContainsMember(member) ? snapshots : null;
    }

    private static bool HasSameTopology(CachedMembership current, MembershipTableSnapshot source)
    {
        if (current.Source.Version != source.Version)
        {
            return false;
        }

        if (current.Source.Entries.Count == source.Entries.Count)
        {
            return true;
        }

        foreach (var member in current.Snapshots.AllMembers.Members)
        {
            if (!source.Entries.ContainsKey(member))
            {
                return false;
            }
        }

        return true;
    }

    private static DisseminationMembershipSnapshots ComputeMembership(
        MembershipTableSnapshot snapshot,
        SiloAddress localSilo,
        DisseminationOverlayOptions overlayOptions,
        DisseminationMembershipSnapshots? previous)
    {
        var members = snapshot.Entries.Values
            .Where(static entry => IsDisseminationMember(entry.Status))
            .OrderBy(static entry => GetStatusRank(entry.Status))
            // Storage providers can round StartTime differently from the originating silo's local snapshot.
            // SiloAddress includes the generation and provides a stable oldest-first order everywhere.
            .ThenBy(static entry => entry.SiloAddress)
            .ToArray();
        var allMembers = ImmutableArray.CreateBuilder<SiloAddress>(members.Length);
        var activeMembers = ImmutableArray.CreateBuilder<SiloAddress>(members.Length);
        foreach (var member in members)
        {
            allMembers.Add(member.SiloAddress);
            if (member.Status == SiloStatus.Active)
            {
                activeMembers.Add(member.SiloAddress);
            }
        }

        return new(
            new DisseminationMembershipSnapshot(
                snapshot.Version,
                localSilo,
                activeMembers.ToImmutable(),
                overlayOptions,
                previous?.ActiveMembers),
            new DisseminationMembershipSnapshot(
                snapshot.Version,
                localSilo,
                allMembers.MoveToImmutable(),
                overlayOptions,
                previous?.AllMembers));
    }

    private static bool IsDisseminationMember(SiloStatus status) =>
        status is SiloStatus.Joining or SiloStatus.Active or SiloStatus.ShuttingDown or SiloStatus.Stopping;

    private static int GetStatusRank(SiloStatus status) => status switch
    {
        SiloStatus.Active => 0,
        SiloStatus.Joining => 1,
        SiloStatus.ShuttingDown => 2,
        SiloStatus.Stopping => 3,
        _ => 4,
    };

    private sealed record CachedMembership(MembershipTableSnapshot Source, DisseminationMembershipSnapshots Snapshots);
}

internal sealed class DisseminationMembershipSnapshots(
    DisseminationMembershipSnapshot activeMembers,
    DisseminationMembershipSnapshot allMembers)
{
    public MembershipVersion MembershipVersion => AllMembers.MembershipVersion;

    public DisseminationMembershipSnapshot ActiveMembers { get; } = activeMembers;

    public DisseminationMembershipSnapshot AllMembers { get; } = allMembers;

    public DisseminationMembershipSnapshot GetSnapshot(DisseminationMembershipScope scope) =>
        scope == DisseminationMembershipScope.ActiveMembers ? ActiveMembers : AllMembers;
}
