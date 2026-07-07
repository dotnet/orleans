using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime.Dissemination;

internal sealed class OrleansDisseminationTransport(
    ILocalSiloDetails localSiloDetails,
    IInternalGrainFactory grainFactory) : IDisseminationTransport
{
    public SiloAddress LocalSilo => localSiloDetails.SiloAddress;

    public Task SendGossip(SiloAddress peer, DisseminationGossipBatch batch, CancellationToken cancellationToken)
    {
        var target = grainFactory.GetSystemTarget<IDisseminationSystemTarget>(Constants.DisseminationSystemTargetType, peer);
        return target.PushGossip(batch, cancellationToken);
    }

    public async ValueTask<DisseminationAntiEntropyResponse> ExchangeAntiEntropy(
        SiloAddress peer,
        DisseminationAntiEntropyRequest request,
        CancellationToken cancellationToken)
    {
        var target = grainFactory.GetSystemTarget<IDisseminationSystemTarget>(Constants.DisseminationSystemTargetType, peer);
        return await target.ExchangeAntiEntropy(request, cancellationToken);
    }
}
