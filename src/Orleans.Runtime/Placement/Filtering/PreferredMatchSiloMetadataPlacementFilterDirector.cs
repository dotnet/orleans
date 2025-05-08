using System;
using System.Collections.Generic;
using System.Linq;
using Orleans.Placement;
using Orleans.Runtime.MembershipService.SiloMetadata;

#nullable enable
namespace Orleans.Runtime.Placement.Filtering;

internal class PreferredMatchSiloMetadataPlacementFilterDirector(
    ILocalSiloDetails localSiloDetails,
    ISiloMetadataCache siloMetadataCache)
    : IPlacementFilterDirector
{
    public IEnumerable<SiloAddress> Filter(PlacementFilterStrategy filterStrategy, PlacementTarget target, IEnumerable<SiloAddress> silos)
    {
        var preferredMatchSiloMetadataPlacementFilterStrategy = filterStrategy as PreferredMatchSiloMetadataPlacementFilterStrategy;
        var minCandidates = preferredMatchSiloMetadataPlacementFilterStrategy?.MinCandidates ?? 1;
        var orderedMetadataKeys = preferredMatchSiloMetadataPlacementFilterStrategy?.OrderedMetadataKeys ?? [];
        
        var localSiloMetadata = siloMetadataCache.GetSiloMetadata(localSiloDetails.SiloAddress).Metadata;

        if (localSiloMetadata.Count == 0)
        {
            return silos;
        }

        var siloList = silos.ToList();
        if (siloList.Count <= minCandidates)
        {
            return siloList;
        }

        // return the list of silos that match the most metadata keys. The first key in the list is the least important.
        // This means that the last key in the list is the most important.
        // If no silos match any metadata keys, return the original list of silos.
        var maxScore = 0;
        var siloScores = new int[siloList.Count];
        var scoreCounts = new int[orderedMetadataKeys.Length+1];
        for (var i = 0; i < siloList.Count; i++)
        {
            var siloMetadata = siloMetadataCache.GetSiloMetadata(siloList[i]).Metadata;
            var siloScore = 0;
            for (var j = orderedMetadataKeys.Length - 1; j >= 0; --j)
            {
                if (siloMetadata.TryGetValue(orderedMetadataKeys[j], out var siloMetadataValue) &&
                    localSiloMetadata.TryGetValue(orderedMetadataKeys[j], out var localSiloMetadataValue) &&
                    siloMetadataValue == localSiloMetadataValue)
                {
                    siloScore = ++siloScores[i];
                    maxScore = Math.Max(maxScore, siloScore);
                }
                else
                {
                    break;
                }
            }
            scoreCounts[siloScore]++;
        }

        if (maxScore == 0)
        {
            return siloList;
        }

        var candidateCount = 0;
        var scoreCutOff = orderedMetadataKeys.Length;
        for (var i = scoreCounts.Length-1; i >= 0; i--)
        {
            candidateCount += scoreCounts[i];
            if (candidateCount >= minCandidates)
            {
                scoreCutOff = i;
                break;
            }
        }

        return siloList.Where((_, i) => siloScores[i] >= scoreCutOff);
    }
}
