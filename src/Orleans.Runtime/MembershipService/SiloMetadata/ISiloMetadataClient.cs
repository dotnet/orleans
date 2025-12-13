using System.Threading.Tasks;

namespace Orleans.Runtime.MembershipService.SiloMetadata;

internal interface ISiloMetadataClient
{
    Task<SiloMetadata> GetSiloMetadata(SiloAddress siloAddress);
}
