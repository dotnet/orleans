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

    public DisseminationMembershipSnapshot CurrentSnapshot
    {
        get
        {
            var membershipSnapshot = membershipManager.CurrentSnapshot;
            var current = Volatile.Read(ref field);
            if (current is not null && current.MembershipVersion == membershipSnapshot.Version)
            {
                return current;
            }

            lock (_membershipLock)
            {
                membershipSnapshot = membershipManager.CurrentSnapshot;
                current = Volatile.Read(ref field);
                if (current is not null && current.MembershipVersion == membershipSnapshot.Version)
                {
                    return current;
                }

                current = ComputeMembership(membershipSnapshot, localSiloDetails.SiloAddress, options.Value.Overlay);
                Volatile.Write(ref field, current);
                return current;
            }
        }
    }

    public Task RefreshMembership(CancellationToken cancellationToken) =>
        membershipManager.Refresh(targetVersion: null, cancellationToken: cancellationToken);

    public async ValueTask<DisseminationMembershipSnapshot?> GetSnapshotContainingMember(
        SiloAddress member,
        CancellationToken cancellationToken)
    {
        var snapshot = CurrentSnapshot;
        if (snapshot.ContainsMember(member))
        {
            return snapshot;
        }

        await RefreshMembership(cancellationToken);
        snapshot = CurrentSnapshot;
        return snapshot.ContainsMember(member) ? snapshot : null;
    }

    private static DisseminationMembershipSnapshot ComputeMembership(
        MembershipTableSnapshot snapshot,
        SiloAddress localSilo,
        DisseminationOverlayOptions overlayOptions)
    {
        var members = snapshot.Entries.Values
            .Where(static entry => IsDisseminationMember(entry.Status))
            .OrderBy(static entry => GetStatusRank(entry.Status))
            .ThenBy(static entry => entry.StartTime)
            .ThenBy(static entry => entry.SiloAddress)
            .ToArray();
        var builder = ImmutableArray.CreateBuilder<SiloAddress>(members.Length);
        foreach (var member in members)
        {
            builder.Add(member.SiloAddress);
        }

        return new DisseminationMembershipSnapshot(
            snapshot.Version,
            localSilo,
            builder.MoveToImmutable(),
            overlayOptions);
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
