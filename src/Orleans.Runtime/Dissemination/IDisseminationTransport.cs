using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime.Dissemination;

internal interface IDisseminationTransport
{
    SiloAddress LocalSilo { get; }

    IReadOnlyList<SiloAddress> GetActivePeers();

    ValueTask<DisseminationCapabilityResponse> GetCapabilities(
        SiloAddress peer,
        DisseminationCapabilityRequest request,
        CancellationToken cancellationToken);

    Task SendGossip(SiloAddress peer, DisseminationGossipBatch batch, CancellationToken cancellationToken);

    ValueTask<DisseminationAntiEntropyResponse> ExchangeAntiEntropy(
        SiloAddress peer,
        DisseminationAntiEntropyRequest request,
        CancellationToken cancellationToken);
}
