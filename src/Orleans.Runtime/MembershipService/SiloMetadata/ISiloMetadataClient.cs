using System.Threading.Tasks;

#nullable enable
namespace Orleans.Runtime.MembershipService.SiloMetadata;

internal interface ISiloMetadataClient
{
    Task<SiloMetadata> GetSiloMetadata(SiloAddress siloAddress);
}
