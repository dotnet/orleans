using System.Threading.Tasks;
using Orleans.GrainDirectory;

namespace Orleans.Runtime.GrainDirectory
{
    internal class GrainLocator
    {
        private readonly GrainLocatorResolver _grainLocatorResolver;

        public GrainLocator(GrainLocatorResolver grainLocatorResolver)
        {
            _grainLocatorResolver = grainLocatorResolver;
        }

        public ValueTask<GrainAddress> Lookup(GrainId grainId) => GetGrainLocator(grainId.Type).Lookup(grainId);

        public Task<GrainAddress> Register(GrainAddress address) => GetGrainLocator(address.GrainId.Type).Register(address);

        public Task Unregister(GrainAddress address, UnregistrationCause cause) => GetGrainLocator(address.GrainId.Type).Unregister(address, cause);

        public bool TryLookupInCache(GrainId grainId, out GrainAddress address) => GetGrainLocator(grainId.Type).TryLookupInCache(grainId, out address);

        public void InvalidateCache(GrainId grainId) => GetGrainLocator(grainId.Type).InvalidateCache(grainId);

        public void InvalidateCache(GrainAddress address) => GetGrainLocator(address.GrainId.Type).InvalidateCache(address);

        public void CachePlacementDecision(GrainAddress address) => GetGrainLocator(address.GrainId.Type).CachePlacementDecision(address);

        private IGrainLocator GetGrainLocator(GrainType grainType) => _grainLocatorResolver.GetGrainLocator(grainType);
    }
}
