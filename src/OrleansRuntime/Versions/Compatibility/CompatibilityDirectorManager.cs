using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.Configuration;
using Orleans.Versions.Compatibility;

namespace Orleans.Runtime.Versions.Compatibility
{
    internal class CompatibilityDirectorManager
    {
        private readonly IServiceProvider serviceProvider;

        public ICompatibilityDirector Default { get; }

        public CompatibilityDirectorManager(IServiceProvider serviceProvider, GlobalConfiguration configuration)
            : this(serviceProvider, configuration.DefaultCompatibilityStrategy)
        {
        }

        public CompatibilityDirectorManager(IServiceProvider serviceProvider, CompatibilityStrategy defaultCompatibilityStrategy)
        {
            this.serviceProvider = serviceProvider;
            Default = ResolveVersionDirector(serviceProvider, defaultCompatibilityStrategy);
        }

        public ICompatibilityDirector GetDirector(int ifaceId)
        {
            return Default;
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
