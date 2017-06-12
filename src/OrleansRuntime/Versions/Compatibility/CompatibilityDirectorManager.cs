using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.Configuration;
using Orleans.Versions.Compatibility;

namespace Orleans.Runtime.Versions.Compatibility
{
    internal class CompatibilityDirectorManager
    {
        private readonly CompatibilityStrategy strategyFromConfig;
        private readonly IServiceProvider serviceProvider;
        private readonly Dictionary<int, ICompatibilityDirector> compatibilityDirectors;

        public ICompatibilityDirector Default { get; private set; }


        public CompatibilityDirectorManager(IServiceProvider serviceProvider, GlobalConfiguration configuration)
            : this(serviceProvider, configuration.DefaultCompatibilityStrategy)
        {
        }

        public CompatibilityDirectorManager(IServiceProvider serviceProvider, CompatibilityStrategy defaultCompatibilityStrategy)
        {
            this.serviceProvider = serviceProvider;
            this.strategyFromConfig = defaultCompatibilityStrategy;
            this.compatibilityDirectors = new Dictionary<int, ICompatibilityDirector>();
            Default = ResolveVersionDirector(serviceProvider, defaultCompatibilityStrategy);
        }

        public ICompatibilityDirector GetDirector(int interfaceId)
        {
            ICompatibilityDirector director;
            return compatibilityDirectors.TryGetValue(interfaceId, out director) 
                ? director 
                : Default;
        }
        public void SetStrategy(CompatibilityStrategy strategy)
        {
            var director = ResolveVersionDirector(this.serviceProvider, strategy ?? this.strategyFromConfig);
            Default = director;
        }

        public void SetStrategy(int interfaceId, CompatibilityStrategy strategy)
        {
            if (strategy == null)
            {
                compatibilityDirectors.Remove(interfaceId);
            }
            else
            {
                var selector = ResolveVersionDirector(this.serviceProvider, strategy);
                compatibilityDirectors[interfaceId] = selector;
            }
        }

        private static ICompatibilityDirector ResolveVersionDirector(IServiceProvider serviceProvider,
            CompatibilityStrategy compatibilityStrategy)
        {
            var strategyType = compatibilityStrategy.GetType();
            var directorType = typeof(ICompatibilityDirector<>).MakeGenericType(strategyType);
            return (ICompatibilityDirector) serviceProvider.GetRequiredService(directorType);
        }
    }
}
