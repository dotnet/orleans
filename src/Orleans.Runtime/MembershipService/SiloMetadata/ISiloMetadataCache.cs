namespace Orleans.Runtime.MembershipService.SiloMetadata;
#nullable enable
public interface ISiloMetadataCache
{
    SiloMetadata GetMetadata(SiloAddress siloAddress);
}