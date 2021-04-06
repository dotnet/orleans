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

        public bool TryLocalLookup(GrainId grainId, out ActivationAddress addresses) => GetGrainLocator(grainId.Type).TryLocalLookup(grainId, out addresses);

        public Task Unregister(ActivationAddress address, UnregistrationCause cause) => GetGrainLocator(address.Grain.Type).Unregister(address, cause);

        private IGrainLocator GetGrainLocator(GrainType grainType) => _grainLocatorResolver.GetGrainLocator(grainType);
    }
}
