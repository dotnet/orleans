using System.Threading.Tasks;
using Orleans.Services;

namespace Orleans.Runtime.MembershipService.SiloMetadata;

public interface ISiloMetadataClient : IGrainServiceClient<ISiloMetadataGrainService>
{
    Task<SiloMetadata> GetSiloMetadata(SiloAddress siloAddress);
}
