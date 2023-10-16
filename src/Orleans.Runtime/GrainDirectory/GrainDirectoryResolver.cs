using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Orleans.GrainDirectory;
using Orleans.Metadata;

namespace Orleans.Runtime.GrainDirectory
{
    internal class GrainDirectoryResolver
    {
        private readonly Dictionary<string, IGrainDirectory> directoryPerName = new Dictionary<string, IGrainDirectory>();
        private readonly ConcurrentDictionary<GrainType, IGrainDirectory> directoryPerType = new();
        private readonly GrainPropertiesResolver grainPropertiesResolver;
        private readonly IGrainDirectoryResolver[] resolvers;
        private readonly Func<GrainType, IGrainDirectory> getGrainDirectoryInternal;

        public GrainDirectoryResolver(
            IServiceProvider serviceProvider,
            GrainPropertiesResolver grainPropertiesResolver,
            IEnumerable<IGrainDirectoryResolver> resolvers)
        {
            this.getGrainDirectoryInternal = GetGrainDirectoryPerType;
            this.resolvers = resolvers.ToArray();

            // Load all registered directories
            var services = serviceProvider.GetService<IKeyedServiceCollection<string, IGrainDirectory>>()?.GetServices(serviceProvider)
                ?? Enumerable.Empty<IKeyedService<string, IGrainDirectory>>();
            foreach (var svc in services)
            {
                this.directoryPerName.Add(svc.Key, svc.GetService(serviceProvider));
            }

            this.directoryPerName.TryGetValue(GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY, out var defaultDirectory);
            this.DefaultGrainDirectory = defaultDirectory;
            this.grainPropertiesResolver = grainPropertiesResolver;
        }

        public IReadOnlyCollection<IGrainDirectory> Directories => this.directoryPerName.Values;

        public static bool HasAnyRegisteredGrainDirectory(IServiceCollection services) => services.Any(svc => svc.ServiceType == typeof(IKeyedService<string, IGrainDirectory>));

        public IGrainDirectory DefaultGrainDirectory { get; }

        public IGrainDirectory Resolve(GrainType grainType) => this.directoryPerType.GetOrAdd(grainType, this.getGrainDirectoryInternal);

        public bool IsUsingDhtDirectory(GrainType grainType) => Resolve(grainType) == null;

        private IGrainDirectory GetGrainDirectoryPerType(GrainType grainType)
        {
            if (this.TryGetNonDefaultGrainDirectory(grainType, out var result))
            {
                return result;
            }

            return this.DefaultGrainDirectory;
        }

        internal bool TryGetNonDefaultGrainDirectory(GrainType grainType, out IGrainDirectory directory)
        {
            this.grainPropertiesResolver.TryGetGrainProperties(grainType, out var properties);

            foreach (var resolver in this.resolvers)
            {
                if (resolver.TryResolveGrainDirectory(grainType, properties, out directory))
                {
                    return true;
                }
            }

            if (properties is not null
                && properties.Properties.TryGetValue(WellKnownGrainTypeProperties.GrainDirectory, out var directoryName)
                && !string.IsNullOrWhiteSpace(directoryName))
            {
                if (this.directoryPerName.TryGetValue(directoryName, out directory))
                {
                    return true;
                }
                else
                {
                    throw new KeyNotFoundException($"Could not resolve grain directory {directoryName} for grain type {grainType}");
                }
            }

            directory = null;
            return false;
        }
    }
}
