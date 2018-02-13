using System;
using Microsoft.Extensions.DependencyInjection;

using Orleans.Configuration;
using Orleans.Messaging;
using Orleans.Runtime.Membership;

namespace Orleans.ConsulUtils
{
    /// <inheritdoc/>
    public class LegacyConsulGatewayListProviderConfigurator : ILegacyGatewayListProviderConfigurator
    {
        /// <inheritdoc/>
        public void ConfigureServices(object configuration, IServiceCollection services)
        {
            services.Configure<ConsulClusteringClientOptions>(
                options =>
                {
                    var reader = new ClientConfigurationReader(configuration);
                    options.Address = new Uri(reader.GetPropertyValue<string>("DataConnectionString"));
                });
            services.AddSingleton<IGatewayListProvider, ConsulGatewayListProvider>();
        }
    }
}
