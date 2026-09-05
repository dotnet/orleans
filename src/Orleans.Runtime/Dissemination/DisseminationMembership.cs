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
    private DisseminationMembershipSnapshots? _currentSnapshots;

    public DisseminationMembershipSnapshots CurrentSnapshots
    {
        get
        {
            var membershipSnapshot = membershipManager.CurrentSnapshot;
            var current = Volatile.Read(ref _currentSnapshots);
            if (current is not null && current.MembershipVersion == membershipSnapshot.Version)
            {
                return current;
            }

            lock (_membershipLock)
            {
                current = Volatile.Read(ref _currentSnapshots);
                if (current is not null && current.MembershipVersion >= membershipSnapshot.Version)
                {
                    return current;
                }

                current = ComputeMembership(membershipSnapshot, localSiloDetails.SiloAddress, options.Value.Overlay, current);
                Volatile.Write(ref _currentSnapshots, current);
                return current;
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
