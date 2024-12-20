using System.Threading.Tasks;
using Orleans.Services;

namespace Orleans.Runtime.MembershipService.SiloMetadata;

[Alias("Orleans.Runtime.MembershipService.SiloMetadata.ISiloMetadataGrainService")]
public interface ISiloMetadataGrainService : IGrainService
{
    [Alias("GetSiloMetadata")]
    Task<SiloMetadata> GetSiloMetadata();
}