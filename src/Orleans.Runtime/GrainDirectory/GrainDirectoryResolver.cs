using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.GrainDirectory;
using Orleans.Utilities;

namespace Orleans.Runtime.GrainDirectory
{
    internal interface IGrainDirectoryResolver
    {
        IReadOnlyCollection<IGrainDirectory> Directories { get; }

        IGrainDirectory Resolve(LegacyGrainId grainId);
    }

    internal class GrainDirectoryResolver : IGrainDirectoryResolver
    {
        private readonly Dictionary<string, IGrainDirectory> directoryPerName = new Dictionary<string, IGrainDirectory>();
        private readonly CachedReadConcurrentDictionary<int, IGrainDirectory> directoryPerType = new CachedReadConcurrentDictionary<int, IGrainDirectory>();
        private readonly GrainTypeManager grainTypeManager;

        public IReadOnlyCollection<IGrainDirectory> Directories => this.directoryPerName.Values;

        public GrainDirectoryResolver(IServiceProvider serviceProvider, GrainTypeManager grainTypeManager)
        {
            this.grainTypeManager = grainTypeManager;

            // Load all registered directories
            var services = serviceProvider
                .GetRequiredService<IKeyedServiceCollection<string, IGrainDirectory>>()
                .GetServices(serviceProvider);
            foreach (var svc in services)
            {
                this.directoryPerName.Add(svc.Key, svc.GetService(serviceProvider));
            }
        }

        public static bool HasAnyRegisteredGrainDirectory(IServiceCollection services) => services.Any(svc => svc.ServiceType == typeof(IKeyedService<string, IGrainDirectory>));

        public IGrainDirectory Resolve(LegacyGrainId grainId) => this.directoryPerType.GetOrAdd(grainId.TypeCode, GetGrainDirectoryPerType);

        private IGrainDirectory GetGrainDirectoryPerType(int grainType)
        {
            if (!this.grainTypeManager.ClusterGrainInterfaceMap.TryGetDirectory(grainType, out var directoryName))
            {
                throw new OrleansException($"Unexpected: Cannot find the directory for grain class {grainType}");
            }

            if (string.IsNullOrEmpty(directoryName))
            {
                return default;
            }

            if (!this.directoryPerName.TryGetValue(directoryName, out var directory))
            {
                if (string.Equals(GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY, directoryName, StringComparison.InvariantCulture))
                {
                    return default;
                }
                throw new OrleansException($"Unexpected: Cannot find the directory named {directoryName}");
            }

            return directory;
        }
    }
}
