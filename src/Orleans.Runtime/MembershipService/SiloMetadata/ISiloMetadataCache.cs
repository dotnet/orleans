#nullable enable
namespace Orleans.Runtime.MembershipService.SiloMetadata;

public interface ISiloMetadataCache
{
    SiloMetadata GetMetadata(SiloAddress siloAddress);
}