using System;
using System.Collections.Generic;
using System.Linq;
using Orleans.Runtime.MembershipService.SiloMetadata;
#nullable enable
namespace Orleans.Runtime.Placement.Filtering;

internal class PreferredSiloMetadataPlacementFilterDirector(
    ILocalSiloDetails localSiloDetails,
    ISiloMetadataCache siloMetadataCache)
    : IPlacementFilterDirector
{
    public IEnumerable<SiloAddress> Filter(PlacementFilterStrategy filterStrategy, PlacementTarget target, IEnumerable<SiloAddress> silos)
    {
        var orderedMetadataKeys = (filterStrategy as PreferredSiloMetadataPlacementFilterStrategy)?.OrderedMetadataKeys ?? [];
        var localSiloMetadata = siloMetadataCache.GetMetadata(localSiloDetails.SiloAddress).Metadata;

        if (localSiloMetadata.Count == 0)
        {
            // yield return all silos if no metadata keys are configured
            foreach (var silo in silos)
            {
                yield return silo;
            }
        }
        else
        {
            // return the list of silos that match the most metadata keys. The first key in the list is the least important.
            // This means that the last key in the list is the most important.
            // If no silos match any metadata keys, return the original list of silos.

            var siloList = silos.ToList();
            var maxScore = 0;
            var siloScores = new int[siloList.Count];
            for (var i = 0; i < siloList.Count; i++)
            {
                var siloMetadata = siloMetadataCache.GetMetadata(siloList[i]).Metadata;
                for (var j = orderedMetadataKeys.Length - 1; j >= 0; --j)
                {
                    if (siloMetadata.TryGetValue(orderedMetadataKeys[j], out var siloMetadataValue) &&
                        localSiloMetadata.TryGetValue(orderedMetadataKeys[j], out var localSiloMetadataValue) &&
                        siloMetadataValue == localSiloMetadataValue)
                    {
                        var newScore = siloScores[i]++;
                        maxScore = Math.Max(maxScore, newScore);
                    }
                    else
                    {
                        break;
                    }
                }
            }

            if (maxScore == 0)
            {
                // yield return all silos if no silos match any metadata keys
                foreach (var silo in siloList)
                {
                    yield return silo;
                }
            }

            // return the list of silos that match the most metadata keys
            for (var i = 0; i < siloScores.Length; i++)
            {
                if (siloScores[i] == maxScore)
                {
                    yield return siloList[i];
                }
            }
        }
    }
}