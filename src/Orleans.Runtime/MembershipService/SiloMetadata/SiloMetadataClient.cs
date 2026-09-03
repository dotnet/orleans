using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime.MembershipService.SiloMetadata;

internal sealed class SiloMetadataClient(IInternalGrainFactory grainFactory) : ISiloMetadataClient
{
    public async Task<SiloMetadata> GetSiloMetadata(
        SiloAddress siloAddress,
        CancellationToken cancellationToken = default)
    {
        var metadataSystemTarget = grainFactory.GetSystemTarget<ISiloMetadataSystemTarget>(Constants.SiloMetadataType, siloAddress);
        var metadata = await metadataSystemTarget.GetSiloMetadata(cancellationToken);
        return metadata;
    }
}
