using System;
using System.Collections.Generic;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Versions.Compatibility;

namespace Orleans.Runtime.Versions.Compatibility
{
    internal class CompatibilityDirectorManager
    {
        private readonly CompatibilityStrategy strategyFromConfig;
        private readonly IServiceProvider serviceProvider;
        private readonly Dictionary<int, ICompatibilityDirector> compatibilityDirectors;

        public ICompatibilityDirector Default { get; private set; }


        public CompatibilityDirectorManager(IServiceProvider serviceProvider, IOptions<GrainVersioningOptions> options)
        {
            this.serviceProvider = serviceProvider;
            this.strategyFromConfig = serviceProvider.GetRequiredServiceByName<CompatibilityStrategy>(options.Value.DefaultCompatibilityStrategy);
            this.compatibilityDirectors = new Dictionary<int, ICompatibilityDirector>();
            Default = ResolveVersionDirector(serviceProvider, this.strategyFromConfig);
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
            return serviceProvider.GetRequiredServiceByKey<Type,ICompatibilityDirector>(strategyType);
        }
    }
}
