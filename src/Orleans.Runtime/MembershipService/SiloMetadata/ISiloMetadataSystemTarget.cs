using System.Threading.Tasks;

#nullable enable
namespace Orleans.Runtime.MembershipService.SiloMetadata;

[Alias("Orleans.Runtime.MembershipService.SiloMetadata.ISiloMetadataSystemTarget")]
internal interface ISiloMetadataSystemTarget : ISystemTarget
{
    [Alias("GetSiloMetadata")]
    Task<SiloMetadata> GetSiloMetadata();
}