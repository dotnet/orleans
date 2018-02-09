using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Messaging;

namespace Orleans.ConsulUtils
{
    /// <inheritdoc/>
    public class LegacyConsulGatewayListProviderConfigurator : ILegacyGatewayListProviderConfigurator
    {
        /// <inheritdoc/>
        public void ConfigureServices(object configuration, IServiceCollection services)
        {
            services.UseConsulGatewayListProvider(options =>
            {
                var reader = new ClientConfigurationReader(configuration);
                options.Address = new Uri(reader.GetPropertyValue<string>("DataConnectionString"));
            });
        }
    }
}
