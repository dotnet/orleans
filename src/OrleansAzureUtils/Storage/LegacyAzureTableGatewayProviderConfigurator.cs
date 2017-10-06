using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Messaging;
using Orleans.Runtime.Configuration;

namespace OrleansAzureUtils.Storage
{
    /// <inheritdoc cref="ILegacyGatewayListProviderConfigurator"/>
    public class LegacyAzureTableGatewayProviderConfigurator : ILegacyGatewayListProviderConfigurator
    {
        /// <inheritdoc cref="ILegacyGatewayListProviderConfigurator"/>
        public void ConfigureServices(ClientConfiguration configuration, IServiceCollection services)
        {
            services.UseAzureTableGatewayProvider(options =>
            {
                options.ConnectionString = configuration.DataConnectionString;
                options.GatewayListRefreshPeriod = configuration.GatewayListRefreshPeriod;
            });
        }
    }
}
