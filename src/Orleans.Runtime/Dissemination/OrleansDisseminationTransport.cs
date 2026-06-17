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

    public IReadOnlyList<SiloAddress> GetActivePeers() => siloStatusOracle.GetApproximateSiloStatuses(true).Keys.ToArray();

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
}
