using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.Configuration;
using Orleans.Versions.Compatibility;

namespace Orleans.Runtime.Versions.Compatibility
{
    internal class CompatibilityDirectorManager
    {
        private readonly IServiceProvider serviceProvider;

        public IVersionCompatibilityDirector Default { get; }

        public CompatibilityDirectorManager(IServiceProvider serviceProvider, GlobalConfiguration configuration)
            : this(serviceProvider, configuration.DefaultVersionCompatibilityStrategy)
        {
        }

        public CompatibilityDirectorManager(IServiceProvider serviceProvider, VersionCompatibilityStrategy defaultVersionCompatibilityStrategy)
        {
            this.serviceProvider = serviceProvider;
            Default = ResolveVersionDirector(serviceProvider, defaultVersionCompatibilityStrategy);
        }

        public IVersionCompatibilityDirector GetDirector(int ifaceId)
        {
            return Default;
        }

        private static IVersionCompatibilityDirector ResolveVersionDirector(IServiceProvider serviceProvider,
            VersionCompatibilityStrategy versionStrategy)
        {
            var policyType = versionStrategy.GetType();
            var directorType = typeof(IVersionCompatibilityDirector<>).MakeGenericType(policyType);
            return (IVersionCompatibilityDirector) serviceProvider.GetRequiredService(directorType);
        }
    }
}
