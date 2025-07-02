#nullable enable
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Orleans.GrainDirectory;

namespace Orleans.Runtime.GrainDirectory
{
    /// <summary>
    /// Provides functionality for locating grain activations in a cluster and registering the location of grain activations.
    /// </summary>
    internal class GrainLocator
    {
        private readonly GrainLocatorResolver _grainLocatorResolver;

        public GrainLocator(GrainLocatorResolver grainLocatorResolver)
        {
            _grainLocatorResolver = grainLocatorResolver;
        }

        public ValueTask<GrainAddress?> Lookup(GrainId grainId) => GetGrainLocator(grainId.Type).Lookup(grainId);

        public Task<GrainAddress?> Register(GrainAddress address, GrainAddress? previousRegistration) => GetGrainLocator(address.GrainId.Type).Register(address, previousRegistration);

        public Task Unregister(GrainAddress address, UnregistrationCause cause) => GetGrainLocator(address.GrainId.Type).Unregister(address, cause);

        public bool TryLookupInCache(GrainId grainId, [NotNullWhen(true)] out GrainAddress? address) => GetGrainLocator(grainId.Type).TryLookupInCache(grainId, out address);

        public void InvalidateCache(GrainId grainId) => GetGrainLocator(grainId.Type).InvalidateCache(grainId);

        public void InvalidateCache(GrainAddress address) => GetGrainLocator(address.GrainId.Type).InvalidateCache(address);

        private IGrainLocator GetGrainLocator(GrainType grainType) => _grainLocatorResolver.GetGrainLocator(grainType);

        public void UpdateCache(GrainId grainId, SiloAddress siloAddress) => GetGrainLocator(grainId.Type).UpdateCache(grainId, siloAddress);

        public void UpdateCache(GrainAddressCacheUpdate update)
        {
            if (update.ValidGrainAddress is { } validAddress)
            {
                Debug.Assert(validAddress.SiloAddress is not null);
                UpdateCache(validAddress.GrainId, validAddress.SiloAddress);
            }
            else
            {
                InvalidateCache(update.InvalidGrainAddress);
            }
        }
    }
}
