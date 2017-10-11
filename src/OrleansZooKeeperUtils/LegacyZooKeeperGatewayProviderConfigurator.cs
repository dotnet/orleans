using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Messaging;
using Orleans.Runtime.Configuration;

namespace OrleansZooKeeperUtils
{
    public class LegacyZooKeeperGatewayProviderConfigurator : ILegacyGatewayListProviderConfigurator
    {
        public void ConfigureServices(ClientConfiguration configuration, IServiceCollection services)
        {
            services.UseZooKeeperGatewayProvider(options =>
            {
                options.ConnectionString = configuration.DataConnectionString;
                options.GatewayListRefreshPeriod = configuration.GatewayListRefreshPeriod;
            });
        }
    }
}
