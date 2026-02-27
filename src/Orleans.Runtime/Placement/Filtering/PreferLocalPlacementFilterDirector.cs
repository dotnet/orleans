using Orleans.Placement;

#nullable enable
namespace Orleans.Runtime.Placement.Filtering;

/// <summary>
/// A placement filter director that prefers the local silo for grain placement.
/// If the local silo is among the candidate silos, only the local silo is returned.
/// Otherwise, all candidate silos are returned unchanged.
/// </summary>
internal class PreferLocalPlacementFilterDirector(ILocalSiloDetails localSiloDetails)
    : IPlacementFilterDirector
{
    /// <inheritdoc />
    public IEnumerable<SiloAddress> Filter(PlacementFilterStrategy filterStrategy, PlacementTarget target, IEnumerable<SiloAddress> silos)
    {
        var localSiloAddress = localSiloDetails.SiloAddress;

        // Fast path: avoid enumeration overhead when the input is a concrete collection (array, list, etc.)
        if (silos is ICollection<SiloAddress> collection)
        {
            return collection.Contains(localSiloAddress) ? [localSiloAddress] : silos;
        }

        // Fallback: materialize while scanning so we don't consume a one-shot enumerable and then try to return it.
        var materialized = new List<SiloAddress>();
        foreach (var silo in silos)
        {
            if (silo.Equals(localSiloAddress))
            {
                return [localSiloAddress];
            }

            materialized.Add(silo);
        }

        return materialized;
    }
}
