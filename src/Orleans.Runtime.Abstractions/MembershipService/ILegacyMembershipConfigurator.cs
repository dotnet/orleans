using Microsoft.Extensions.DependencyInjection;
using System;
using System.Reflection;

using Orleans.Hosting;

namespace Orleans.Runtime.MembershipService
{
    /// <summary>
    /// LegacyMembershipConfigurator configure membership table in the legacy way, which is from global configuration
    /// </summary>
    public interface ILegacyMembershipConfigurator : ILegacySiloConfigurationAdapter
    {
    }

    /// <summary>
    /// Configures reminders using legacy configuration.
    /// </summary>
    public interface ILegacyReminderTableAdapter : ILegacySiloConfigurationAdapter
    {
    }

    /// <summary>
    /// Configures a silo host using legacy configuration.
    /// </summary>
    public interface ILegacySiloConfigurationAdapter
    {
        /// <summary>
        /// Configures the provided <paramref name="builder"/> using <paramref name="configuration"/>.
        /// </summary>
        /// <param name="configuration">The legacy GlobalConfiguration object.</param>
        /// <param name="builder">The silo host builder.</param>
        void Configure(object configuration, ISiloHostBuilder builder);
    }

    /// <summary>
    /// Wrapper for legacy config.  Should not be used for any new developent, only adapting legacy systems.
    /// </summary>
    public class GlobalConfigurationReader
    {
        private readonly object configuration;
        private readonly Type type;
        
        public GlobalConfigurationReader(object configuration)
        {
            this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            this.type = configuration.GetType();
            if(this.type.Name != "GlobalConfiguration") throw new ArgumentException($"GlobalConfiguration expected", nameof(configuration));
        }

        public T GetPropertyValue<T>(string propertyName)
        {
            try
            {
                MethodInfo getMethod = this.type.GetProperty(propertyName).GetGetMethod();
                return (T)getMethod.Invoke(this.configuration, null);
            } catch(Exception ex)
            {
                throw new ArgumentOutOfRangeException($"Could not get property {propertyName} of type {typeof(T)} from configuration of type {this.configuration.GetType()}", ex);
            }
        }
    }
}
