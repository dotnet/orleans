using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.GrainDirectory;

namespace Orleans.Runtime.Placement
{
    public interface IPlacementContext
    {
        IList<SiloAddress> GetCompatibleSilos(PlacementTarget target);

        SiloAddress LocalSilo { get; }

        SiloStatus LocalSiloStatus { get; }
    }
}
