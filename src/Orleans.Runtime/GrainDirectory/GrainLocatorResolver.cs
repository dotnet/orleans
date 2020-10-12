using System;
using System.Collections.Concurrent;
using Orleans.GrainDirectory;

namespace Orleans.Runtime.GrainDirectory
{
    internal class GrainLocatorResolver
    {
        private readonly ConcurrentDictionary<GrainType, IGrainLocator> resolvedLocators = new ConcurrentDictionary<GrainType, IGrainLocator>(GrainType.Comparer.Instance);
        private readonly Func<GrainType, IGrainLocator> getLocatorInternal;
        private readonly GrainDirectoryResolver grainDirectoryResolver;
        private readonly CachedGrainLocator cachedGrainLocator;
        private readonly DhtGrainLocator dhtGrainLocator;
        private readonly ClientGrainLocator clientGrainLocator;

        public GrainLocatorResolver(
            GrainDirectoryResolver grainDirectoryResolver,
            CachedGrainLocator cachedGrainLocator,
            DhtGrainLocator dhtGrainLocator,
            ClientGrainLocator clientGrainLocator)
        {
            this.getLocatorInternal = GetGrainLocatorInternal;
            this.grainDirectoryResolver = grainDirectoryResolver;
            this.cachedGrainLocator = cachedGrainLocator;
            this.dhtGrainLocator = dhtGrainLocator;
            this.clientGrainLocator = clientGrainLocator;
        }

        public IGrainLocator GetGrainLocator(GrainType grainType) => resolvedLocators.GetOrAdd(grainType, this.getLocatorInternal);

        public IGrainLocator GetGrainLocatorInternal(GrainType grainType)
        {
            IGrainLocator result;
            if (grainType.IsClient())
            {
                result = this.clientGrainLocator;
            }
            else if (this.grainDirectoryResolver.HasNonDefaultDirectory(grainType))
            {
                result = this.cachedGrainLocator;
            }
            else
            {
                result = this.dhtGrainLocator;
            }

            return result;
        }
    }
}
