using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Messaging;
using Orleans.Runtime.Configuration;

namespace OrleansConsulUtils
{
    /// <inheritdoc cref="ILegacyGatewayListProviderConfigurator"/>
    public class LegacyConsulGatewayProviderConfigurator : ILegacyGatewayListProviderConfigurator
    {
        /// <inheritdoc cref="ILegacyGatewayListProviderConfigurator"/>
        public void ConfigureServices(ClientConfiguration configuration, IServiceCollection services)
        {
            services.UseConsulGatewayProvider(options =>
            {
                options.ConnectionString = configuration.DataConnectionString;
                options.GatewayListRefreshPeriod = configuration.GatewayListRefreshPeriod;
            });
        }
    }
}
