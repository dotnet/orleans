using System;
using System.Collections.Immutable;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime.Dissemination;

internal sealed class OrleansDisseminationTransport(
    ILocalSiloDetails localSiloDetails,
    ISiloStatusOracle siloStatusOracle,
    IInternalGrainFactory grainFactory) : IDisseminationTransport
{
    public SiloAddress LocalSilo => localSiloDetails.SiloAddress;

    public DisseminationMembership GetMembership()
    {
        var allMembers = ImmutableArray.CreateBuilder<SiloAddress>();
        var activeMembers = ImmutableArray.CreateBuilder<SiloAddress>();
        foreach (var (siloAddress, status) in siloStatusOracle.GetApproximateSiloStatuses(onlyActive: false))
        {
            if (IsDisseminationParticipant(status))
            {
                allMembers.Add(siloAddress);
            }

            if (status == SiloStatus.Active)
            {
                activeMembers.Add(siloAddress);
            }
        }

        allMembers.Sort(static (left, right) => left.CompareTo(right));
        activeMembers.Sort(static (left, right) => left.CompareTo(right));
        return new DisseminationMembership(allMembers.ToImmutable(), activeMembers.ToImmutable());
    }

    public async ValueTask<DisseminationCapabilityResponse> GetCapabilities(
        SiloAddress peer,
        DisseminationCapabilityRequest request,
        CancellationToken cancellationToken)
    {
        var target = grainFactory.GetSystemTarget<IDisseminationSystemTarget>(Constants.DisseminationSystemTargetType, peer);
        return await target.GetCapabilities(request).WaitAsync(cancellationToken);
    }

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
}
