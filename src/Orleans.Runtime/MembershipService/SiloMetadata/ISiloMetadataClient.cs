using System.Threading.Tasks;
using Orleans.Services;

#nullable enable
namespace Orleans.Runtime.MembershipService.SiloMetadata;

public interface ISiloMetadataClient : IGrainServiceClient<ISiloMetadataGrainService>
{
    Task<SiloMetadata> GetSiloMetadata(SiloAddress siloAddress);
}
