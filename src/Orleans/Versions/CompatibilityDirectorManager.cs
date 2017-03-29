using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.Configuration;
using Orleans.Versions.Placement;

namespace Orleans.Versions.Compatibility
{
    public class CompatibilityDirectorManager
    {
        private readonly IServiceProvider serviceProvider;

        public ICompatibilityDirector Default { get; }

        public CompatibilityDirectorManager(IServiceProvider serviceProvider, GlobalConfiguration configuration)
            : this(serviceProvider, BackwardCompatible.Singleton)
        {
        }

        public CompatibilityDirectorManager(IServiceProvider serviceProvider, CompatibilityStrategy defaultCompatibilityStrategy)
        {
            this.serviceProvider = serviceProvider;
            Default = ResolveVersionDirector(serviceProvider, defaultCompatibilityStrategy);
        }

        public ICompatibilityDirector GetPolicy(int ifaceId)
        {
            return Default;
        }

        private static ICompatibilityDirector ResolveVersionDirector(IServiceProvider serviceProvider,
            CompatibilityStrategy versionStrategy)
        {
            var policyType = versionStrategy.GetType();
            var directorType = typeof(IVersionDirector<>).MakeGenericType(policyType);
            return (ICompatibilityDirector) serviceProvider.GetRequiredService(directorType);
        }
    }
}
