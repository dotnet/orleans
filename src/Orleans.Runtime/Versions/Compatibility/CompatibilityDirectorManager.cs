using System;
using System.Collections.Concurrent;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Versions.Compatibility;

namespace Orleans.Runtime.Versions.Compatibility
{
    internal sealed class CompatibilityDirectorManager
    {
        private readonly CompatibilityStrategy strategyFromConfig;
        private readonly IServiceProvider serviceProvider;
        private readonly ConcurrentDictionary<GrainInterfaceType, ICompatibilityDirector> compatibilityDirectors;
        private ICompatibilityDirector defaultDirector;

        public ICompatibilityDirector Default
        {
            get => Volatile.Read(ref defaultDirector);
            private set => Volatile.Write(ref defaultDirector, value);
        }


        public CompatibilityDirectorManager(IServiceProvider serviceProvider, IOptions<GrainVersioningOptions> options)
        {
            this.serviceProvider = serviceProvider;
            this.strategyFromConfig = serviceProvider.GetRequiredKeyedService<CompatibilityStrategy>(options.Value.DefaultCompatibilityStrategy);
            this.compatibilityDirectors = new();
            defaultDirector = ResolveVersionDirector(serviceProvider, this.strategyFromConfig);
        }

        public ICompatibilityDirector GetDirector(GrainInterfaceType interfaceType)
        {
            return compatibilityDirectors.TryGetValue(interfaceType, out var director)
                ? director
                : Default;
        }
        public void SetStrategy(CompatibilityStrategy? strategy)
        {
            var director = ResolveVersionDirector(this.serviceProvider, strategy ?? this.strategyFromConfig);
            Default = director;
        }

        public void SetStrategy(GrainInterfaceType interfaceType, CompatibilityStrategy? strategy)
        {
            if (strategy == null)
            {
                compatibilityDirectors.TryRemove(interfaceType, out _);
            }
            else
            {
                var selector = ResolveVersionDirector(this.serviceProvider, strategy);
                compatibilityDirectors[interfaceType] = selector;
            }
        }

        private static ICompatibilityDirector ResolveVersionDirector(IServiceProvider serviceProvider,
            CompatibilityStrategy compatibilityStrategy)
        {
            var strategyType = compatibilityStrategy.GetType();
            return serviceProvider.GetRequiredKeyedService<ICompatibilityDirector>(strategyType);
        }
    }
}
