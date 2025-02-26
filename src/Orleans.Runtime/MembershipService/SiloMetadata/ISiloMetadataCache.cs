#nullable enable

namespace Orleans.Runtime.MembershipService.SiloMetadata;

public interface ISiloMetadataCache
{
    SiloMetadata GetSiloMetadata(SiloAddress siloAddress);
}