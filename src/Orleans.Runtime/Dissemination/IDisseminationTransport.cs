using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime.Dissemination;

internal interface IDisseminationTransport
{
    SiloAddress LocalSilo { get; }

    DisseminationMembership GetMembership();

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

internal readonly record struct DisseminationMembership(
    ImmutableArray<SiloAddress> AllMembers,
    ImmutableArray<SiloAddress> ActiveMembers);
