using System.Collections.Generic;
using System.Linq;
using Orleans.Placement;
using Orleans.Runtime.MembershipService.SiloMetadata;

#nullable enable
namespace Orleans.Runtime.Placement.Filtering;

internal class RequiredMatchSiloMetadataPlacementFilterDirector(ILocalSiloDetails localSiloDetails, ISiloMetadataCache siloMetadataCache)
    : IPlacementFilterDirector
{
    public IEnumerable<SiloAddress> Filter(PlacementFilterStrategy filterStrategy, PlacementTarget target, IEnumerable<SiloAddress> silos)
    {
        var metadataKeys = (filterStrategy as RequiredMatchSiloMetadataPlacementFilterStrategy)?.MetadataKeys ?? [];

        // yield return all silos if no silos match any metadata keys
        if (metadataKeys.Length == 0)
        {
            return silos;
        }

        var localMetadata = siloMetadataCache.GetSiloMetadata(localSiloDetails.SiloAddress);
        var localRequiredMetadata = GetMetadata(localMetadata, metadataKeys);

        return silos.Where(silo =>
        {
            var remoteMetadata = siloMetadataCache.GetSiloMetadata(silo);
            return DoesMetadataMatch(localRequiredMetadata, remoteMetadata, metadataKeys);
        });
    }

    private static bool DoesMetadataMatch(string?[] localMetadata, SiloMetadata siloMetadata, string[] metadataKeys)
    {
        for (var i = 0; i < metadataKeys.Length; i++)
        {
            if (localMetadata[i] != siloMetadata.Metadata.GetValueOrDefault(metadataKeys[i]))
            {
                return false;
            }
        }

        return true;
    }
    private static string?[] GetMetadata(SiloMetadata siloMetadata, string[] metadataKeys)
    {
        var result = new string?[metadataKeys.Length];
        for (var i = 0; i < metadataKeys.Length; i++)
        {
            result[i] = siloMetadata.Metadata.GetValueOrDefault(metadataKeys[i]);
        }

        return result;
    }
}
