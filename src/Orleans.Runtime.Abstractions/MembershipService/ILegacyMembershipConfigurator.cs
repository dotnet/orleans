using Microsoft.Extensions.DependencyInjection;
using System;
using System.Reflection;

namespace Orleans.Runtime.MembershipService
{
    /// <summary>
    /// LegacyMembershipConfigurator configure membership table in the legacy way, which is from global configuration
    /// </summary>
    public interface ILegacyMembershipConfigurator
    {
        /// <summary>
        /// Configure the membership table in the legacy way 
        /// </summary>
        void ConfigureServices(object configuration, IServiceCollection services);
    }

    /// <summary>
    /// Wapper for legacy config.  Should not be used for any new developent, only adapting legacy systems.
    /// </summary>
    public class GlobalConfigurationReader
    {
        private readonly object configuration;
        private readonly Type type;
        
        public GlobalConfigurationReader(object configuration)
        {
            this.configuration = configuration;
            this.type = configuration.GetType();
            if(this.type.Name != "GlobalConfiguration") throw new ArgumentException($"GlobalConfiguration expected", nameof(configuration));
        }

        public T GetPropertyValue<T>(string propertyName)
        {
            MethodInfo getMethod = this.type.GetProperty(propertyName).GetGetMethod();
            return (T)getMethod.Invoke(this.configuration, null);
        }
    }
}
