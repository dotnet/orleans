using System.Threading.Tasks;

#nullable enable
namespace Orleans.Runtime.MembershipService.SiloMetadata;

internal sealed class SiloMetadataClient(IInternalGrainFactory grainFactory) : ISiloMetadataClient
{
    public async Task<SiloMetadata> GetSiloMetadata(SiloAddress siloAddress)
    {
        var metadataSystemTarget = grainFactory.GetSystemTarget<ISiloMetadataSystemTarget>(Constants.SiloMetadataType, siloAddress);
        var metadata = await metadataSystemTarget.GetSiloMetadata();
        return metadata;
    }
}