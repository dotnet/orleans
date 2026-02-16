using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Orleans.GrainDirectory;

namespace Orleans.Runtime.GrainDirectory
{
    internal sealed class GrainLocatorResolver
    {
        private readonly ConcurrentDictionary<GrainType, IGrainLocator> resolvedLocators = new();
        private readonly Func<GrainType, IGrainLocator> getLocatorInternal;
        private readonly IServiceProvider _servicesProvider;
        private readonly GrainDirectoryResolver grainDirectoryResolver;
        private readonly CachedGrainLocator cachedGrainLocator;
        private readonly IGrainLocator defaultGrainLocator;
        private ClientGrainLocator _clientGrainLocator;

        public GrainLocatorResolver(
            IServiceProvider servicesProvider,
            GrainDirectoryResolver grainDirectoryResolver,
            CachedGrainLocator cachedGrainLocator,
            IGrainLocator grainLocator)
        {
            this.getLocatorInternal = GetGrainLocatorInternal;
            _servicesProvider = servicesProvider;
            this.grainDirectoryResolver = grainDirectoryResolver;
            this.cachedGrainLocator = cachedGrainLocator;
            this.defaultGrainLocator = grainLocator;
        }

        public IGrainLocator GetGrainLocator(GrainType grainType) => resolvedLocators.GetOrAdd(grainType, this.getLocatorInternal);

        public IGrainLocator GetGrainLocatorInternal(GrainType grainType)
        {
            IGrainLocator result;
            if (grainType.IsClient())
            {
                result = this._clientGrainLocator ??= _servicesProvider.GetRequiredService<ClientGrainLocator>();
            }
            else if (this.grainDirectoryResolver.IsUsingDefaultDirectory(grainType))
            {
                result = this.defaultGrainLocator;
            }
            else
            {
                result = this.cachedGrainLocator;
            }

            return result;
        }
    }
}
