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

        public ValueTask<ActivationAddress> Lookup(GrainId grainId) => GetGrainLocator(grainId.Type).Lookup(grainId);

        public Task<ActivationAddress> Register(ActivationAddress address) => GetGrainLocator(address.Grain.Type).Register(address);

        public Task Unregister(ActivationAddress address, UnregistrationCause cause) => GetGrainLocator(address.Grain.Type).Unregister(address, cause);

        public bool TryLookupInCache(GrainId grainId, out ActivationAddress address) => GetGrainLocator(grainId.Type).TryLookupInCache(grainId, out address);

        public void InvalidateCache(GrainId grainId) => GetGrainLocator(grainId.Type).InvalidateCache(grainId);

        public void InvalidateCache(ActivationAddress address) => GetGrainLocator(address.Grain.Type).InvalidateCache(address);

        public void CachePlacementDecision(ActivationAddress address) => GetGrainLocator(address.Grain.Type).CachePlacementDecision(address);

        private IGrainLocator GetGrainLocator(GrainType grainType) => _grainLocatorResolver.GetGrainLocator(grainType);
    }
}
