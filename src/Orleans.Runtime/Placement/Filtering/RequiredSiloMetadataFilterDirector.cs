using System.Collections.Generic;
using System.Linq;
using Orleans.Runtime.MembershipService.SiloMetadata;

namespace Orleans.Runtime.Placement.Filtering;

internal class RequiredSiloMetadataFilterDirector(ILocalSiloDetails localSiloDetails, ISiloMetadataCache siloMetadataCache)
    : IPlacementFilterDirector
{
    public IEnumerable<SiloAddress> Filter(PlacementFilterStrategy filterStrategy, PlacementTarget target, IEnumerable<SiloAddress> silos)
    {
        var metadataKeys = (filterStrategy as RequiredSiloMetadataPlacementFilterStrategy)?.MetadataKeys ?? [];

        // yield return all silos if no silos match any metadata keys
        if (metadataKeys.Length == 0)
        {
            foreach (var silo in silos)
            {
                yield return silo;
            }
        }
        else
        {
            var localMetadata = siloMetadataCache.GetMetadata(localSiloDetails.SiloAddress);
            var localRequiredMetadata = GetMetadata(localMetadata, metadataKeys);
            
            foreach (var silo in silos)
            {
                var remoteMetadata = siloMetadataCache.GetMetadata(silo);
                if(DoesMetadataMatch(localRequiredMetadata, remoteMetadata, metadataKeys))
                {
                    yield return silo;
                }
            }
        }
    }

    private static bool DoesMetadataMatch(string[] localMetadata, SiloMetadata siloMetadata, string[] metadataKeys)
    {
        for (var i = 0; i < metadataKeys.Length; i++)
        {
            if(localMetadata[i] != siloMetadata.Metadata?.GetValueOrDefault(metadataKeys[i]))
            {
                return false;
            }
        }

        return true;
    }
    private static string[] GetMetadata(SiloMetadata siloMetadata, string[] metadataKeys)
    {
        var result = new string[metadataKeys.Length];
        for (var i = 0; i < metadataKeys.Length; i++)
        {
            result[i] = siloMetadata.Metadata?.GetValueOrDefault(metadataKeys[i]);
        }
        return result;
    }
}