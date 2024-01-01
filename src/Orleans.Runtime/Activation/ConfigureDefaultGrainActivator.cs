using System;
using Orleans.Metadata;

namespace Orleans.Runtime
{
    internal class ConfigureDefaultGrainActivator : IConfigureGrainTypeComponents
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly GrainClassMap _grainClassMap;

        public ConfigureDefaultGrainActivator(GrainClassMap grainClassMap, IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
            _grainClassMap = grainClassMap;
        }

        public void Configure(GrainType grainType, GrainProperties properties, GrainTypeSharedContext shared)
        {
            if (shared.GetComponent<IGrainActivator>() is object) return;

            if (!_grainClassMap.TryGetGrainClass(grainType, out var grainClass))
            {
                return;
            }

            var instanceActivator = new DefaultGrainActivator(_serviceProvider, grainClass);
            shared.SetComponent<IGrainActivator>(instanceActivator);
        }
    }
}