using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Placement;
using Orleans.Runtime.MembershipService.SiloMetadata;

namespace Orleans.Runtime.Placement.Filtering;

internal class RequiredMatchSiloMetadataPlacementFilterDirector(ILocalSiloDetails localSiloDetails, ISiloMetadataCache siloMetadataCache)
    : IPlacementFilterDirector
{
    public Task<IReadOnlyList<SiloAddress>> FilterAsync(
        PlacementFilterStrategy filterStrategy,
        PlacementTarget target,
        IReadOnlyList<SiloAddress> silos,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var metadataKeys = (filterStrategy as RequiredMatchSiloMetadataPlacementFilterStrategy)?.MetadataKeys ?? [];

        if (metadataKeys.Length == 0)
        {
            return Task.FromResult(silos);
        }

        var localMetadata = siloMetadataCache.GetSiloMetadata(localSiloDetails.SiloAddress);
        var localRequiredMetadata = GetMetadata(localMetadata, metadataKeys);

        var result = new List<SiloAddress>();
        foreach (var silo in silos)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remoteMetadata = siloMetadataCache.GetSiloMetadata(silo);
            if (DoesMetadataMatch(localRequiredMetadata, remoteMetadata, metadataKeys))
            {
                result.Add(silo);
            }
        }

        return Task.FromResult<IReadOnlyList<SiloAddress>>(result);
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
