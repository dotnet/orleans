using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.GrainDirectory
{
    /// <summary>
    /// Used to locate Grain activation in the cluster
    /// </summary>
    public interface IGrainLocator
    {
        Task<ActivationAddress> Register(ActivationAddress address);

        Task Unregister(ActivationAddress address, UnregistrationCause cause);

        ValueTask<ActivationAddress> Lookup(GrainId grainId);

        void CachePlacementDecision(ActivationAddress address);

        void InvalidateCache(GrainId grainId);

        void InvalidateCache(ActivationAddress address);

        bool TryLookupInCache(GrainId grainId, out ActivationAddress address);
    }
}
