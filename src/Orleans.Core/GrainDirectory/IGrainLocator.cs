using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.GrainDirectory
{
    /// <summary>
    /// Used to locate Grain activation in the cluster
    /// </summary>
    public interface IGrainLocator
    {
        Task<GrainAddress> Register(GrainAddress address);

        Task Unregister(GrainAddress address, UnregistrationCause cause);

        ValueTask<GrainAddress> Lookup(GrainId grainId);

        void CachePlacementDecision(GrainAddress address);

        void InvalidateCache(GrainId grainId);

        void InvalidateCache(GrainAddress address);

        bool TryLookupInCache(GrainId grainId, out GrainAddress address);
    }
}
