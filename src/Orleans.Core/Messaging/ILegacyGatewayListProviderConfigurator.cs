using System;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Messaging
{
    /// <summary>
    /// LegacyGatewayProviderConfigurator configure GatewayListProvider in the legacy way, which is from ClientConfiguration
    /// </summary>
    public interface ILegacyGatewayListProviderConfigurator
    {
        void ConfigureServices(object configuration, IServiceCollection services);
    }

    /// <summary>
    /// Wrapper for legacy client config.  Should not be used for any new developent, only adapting legacy systems.
    /// </summary>
    public class ClientConfigurationReader
    {
        private readonly object configuration;
        private readonly Type type;

        public ClientConfigurationReader(object configuration)
        {
            this.configuration = configuration;
            this.type = configuration.GetType();
            if (this.type.Name != "ClientConfiguration") throw new ArgumentException($"ClientConfiguration expected", nameof(configuration));
        }

        public T GetPropertyValue<T>(string propertyName)
        {
            MethodInfo getMethod = this.type.GetProperty(propertyName).GetGetMethod();
            return (T)getMethod.Invoke(this.configuration, null);
        }
    }

}
