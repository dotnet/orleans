#nullable enable
using System.Diagnostics.CodeAnalysis;
using System.Threading;
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

        public ValueTask<GrainAddress?> Lookup(GrainId grainId, CancellationToken cancellationToken) => GetGrainLocator(grainId.Type).Lookup(grainId, cancellationToken);

        public Task<GrainAddress> Register(GrainAddress address, GrainAddress? previousRegistration, CancellationToken cancellationToken) => GetGrainLocator(address.GrainId.Type).Register(address, previousRegistration, cancellationToken);

        public Task Unregister(GrainAddress address, UnregistrationCause cause, CancellationToken cancellationToken) => GetGrainLocator(address.GrainId.Type).Unregister(address, cause, cancellationToken);

        public bool TryLookupInCache(GrainId grainId, [NotNullWhen(true)] out GrainAddress? address) => GetGrainLocator(grainId.Type).TryLookupInCache(grainId, out address);

        public void InvalidateCache(GrainId grainId) => GetGrainLocator(grainId.Type).InvalidateCache(grainId);

        public void InvalidateCache(GrainAddress address) => GetGrainLocator(address.GrainId.Type).InvalidateCache(address);

        public void CachePlacementDecision(GrainId grainId, SiloAddress siloAddress) => GetGrainLocator(grainId.Type).CachePlacementDecision(grainId, siloAddress);

        private IGrainLocator GetGrainLocator(GrainType grainType) => _grainLocatorResolver.GetGrainLocator(grainType);
    }
}
