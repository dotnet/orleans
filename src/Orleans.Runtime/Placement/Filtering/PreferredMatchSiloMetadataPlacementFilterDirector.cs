using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Placement;
using Orleans.Runtime.MembershipService.SiloMetadata;

namespace Orleans.Runtime.Placement.Filtering;

internal class PreferredMatchSiloMetadataPlacementFilterDirector(
    ILocalSiloDetails localSiloDetails,
    ISiloMetadataCache siloMetadataCache)
    : IPlacementFilterDirector
{
    public Task<IReadOnlyList<SiloAddress>> FilterAsync(
        PlacementFilterStrategy filterStrategy,
        PlacementTarget target,
        IReadOnlyList<SiloAddress> silos,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var preferredMatchSiloMetadataPlacementFilterStrategy = filterStrategy as PreferredMatchSiloMetadataPlacementFilterStrategy;
        var minCandidates = preferredMatchSiloMetadataPlacementFilterStrategy?.MinCandidates ?? 1;
        var orderedMetadataKeys = preferredMatchSiloMetadataPlacementFilterStrategy?.OrderedMetadataKeys ?? [];

        var localSiloMetadata = siloMetadataCache.GetSiloMetadata(localSiloDetails.SiloAddress).Metadata;

        if (localSiloMetadata.Count == 0)
        {
            return Task.FromResult(silos);
        }

        var siloList = silos;
        if (siloList.Count <= minCandidates)
        {
            return Task.FromResult(siloList);
        }

        // return the list of silos that match the most metadata keys. The first key in the list is the least important.
        // This means that the last key in the list is the most important.
        // If no silos match any metadata keys, return the original list of silos.
        var maxScore = 0;
        var siloScores = new int[siloList.Count];
        var scoreCounts = new int[orderedMetadataKeys.Length + 1];
        for (var i = 0; i < siloList.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            return Task.FromResult(siloList);
        }

        var candidateCount = 0;
        var scoreCutOff = orderedMetadataKeys.Length;
        for (var i = scoreCounts.Length - 1; i >= 0; i--)
        {
            candidateCount += scoreCounts[i];
            if (candidateCount >= minCandidates)
            {
                scoreCutOff = i;
                break;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<SiloAddress>>(siloList.Where((_, i) => siloScores[i] >= scoreCutOff).ToArray());
    }
}
