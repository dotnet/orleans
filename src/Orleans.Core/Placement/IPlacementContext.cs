using System.Collections.Generic;

namespace Orleans.Runtime.Placement
{
    public interface IPlacementContext
    {
        SiloAddress[] GetCompatibleSilos(PlacementTarget target);

        IReadOnlyDictionary<ushort, SiloAddress[]> GetCompatibleSilosWithVersions(PlacementTarget target);

        SiloAddress LocalSilo { get; }

        SiloStatus LocalSiloStatus { get; }
    }
}
