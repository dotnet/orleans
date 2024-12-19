using System;
using System.Threading.Tasks;
using Orleans.Runtime.Services;

namespace Orleans.Runtime.MembershipService.SiloMetadata;

public class SiloMetadataClient(IServiceProvider serviceProvider)
    : GrainServiceClient<ISiloMetadataGrainService>(serviceProvider), ISiloMetadataClient
{
    public async Task<SiloMetadata> GetSiloMetadata(SiloAddress siloAddress)
    {
        var grainService = GetGrainService(siloAddress);
        var metadata = await grainService.GetSiloMetadata();
        return metadata;
    }
}