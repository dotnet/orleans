using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.GrainDirectory;
using Orleans.Metadata;

namespace Orleans.Runtime.GrainDirectory
{
    internal class GenericGrainDirectoryResolver : IGrainDirectoryResolver
    {
        private readonly IServiceProvider _services;
        private GrainDirectoryResolver _resolver;

        public GenericGrainDirectoryResolver(IServiceProvider services)
        {
            _services = services;
        }

        public bool TryResolveGrainDirectory(GrainType grainType, GrainProperties properties, out IGrainDirectory grainDirectory)
        {
            if (GenericGrainType.TryParse(grainType, out var constructed) && constructed.IsConstructed)
            {
                var generic = constructed.GetUnconstructedGrainType().GrainType;
                var resolver = GetResolver();
                if (resolver.TryGetNonDefaultGrainDirectory(generic, out grainDirectory))
                {
                    return true;
                }
            }

            grainDirectory = default;
            return false;
        }

        private GrainDirectoryResolver GetResolver() => _resolver ??= _services.GetRequiredService<GrainDirectoryResolver>();
    }
}
