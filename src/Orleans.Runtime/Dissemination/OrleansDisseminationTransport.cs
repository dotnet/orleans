using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime.MembershipService;

namespace Orleans.Runtime.Dissemination;

internal sealed class OrleansDisseminationTransport(
    ILocalSiloDetails localSiloDetails,
    IMembershipManager membershipManager,
    IInternalGrainFactory grainFactory) : IDisseminationTransport
{
    public SiloAddress LocalSilo => localSiloDetails.SiloAddress;

    public DisseminationMembership GetMembership()
    {
        var entries = membershipManager.CurrentSnapshot.Entries.Values;
        var allMembers = entries
            .Where(static entry => IsDisseminationParticipant(entry.Status))
            .OrderBy(static entry => GetStatusRank(entry.Status))
            .ThenBy(static entry => entry.StartTime)
            .ThenBy(static entry => entry.SiloAddress)
            .Select(static entry => entry.SiloAddress)
            .ToImmutableArray();
        var activeMembers = entries
            .Where(static entry => entry.Status == SiloStatus.Active)
            .OrderBy(static entry => entry.StartTime)
            .ThenBy(static entry => entry.SiloAddress)
            .Select(static entry => entry.SiloAddress)
            .ToImmutableArray();
        return new DisseminationMembership(allMembers, activeMembers);
    }

    public Task RefreshMembership(CancellationToken cancellationToken) =>
        membershipManager.Refresh(targetVersion: null, cancellationToken);

    public Task SendGossip(SiloAddress peer, DisseminationGossipBatch batch, CancellationToken cancellationToken)
    {
        var target = grainFactory.GetSystemTarget<IDisseminationSystemTarget>(Constants.DisseminationSystemTargetType, peer);
        return target.PushGossip(batch).WaitAsync(cancellationToken);
    }

    public async ValueTask<DisseminationAntiEntropyResponse> ExchangeAntiEntropy(
        SiloAddress peer,
        DisseminationAntiEntropyRequest request,
        CancellationToken cancellationToken)
    {
        var target = grainFactory.GetSystemTarget<IDisseminationSystemTarget>(Constants.DisseminationSystemTargetType, peer);
        return await target.ExchangeAntiEntropy(request).WaitAsync(cancellationToken);
    }

    private static bool IsDisseminationParticipant(SiloStatus status) =>
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
