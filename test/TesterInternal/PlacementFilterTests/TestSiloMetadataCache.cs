using Orleans.Runtime.MembershipService.SiloMetadata;

namespace UnitTests.PlacementFilterTests;

internal class TestSiloMetadataCache : ISiloMetadataCache
{
    private readonly Dictionary<SiloAddress, SiloMetadata> _metadata;

    public TestSiloMetadataCache(Dictionary<SiloAddress, SiloMetadata> metadata)
    {
        _metadata = metadata;
    }

    public SiloMetadata GetSiloMetadata(SiloAddress siloAddress) => _metadata.GetValueOrDefault(siloAddress) ?? SiloMetadata.Empty;
}