using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Messaging;
using Orleans.Runtime.Configuration;

namespace OrleansZooKeeperUtils
{
    /// <inheritdoc/>
    public class LegacyZooKeeperGatewayListProviderConfigurator : ILegacyGatewayListProviderConfigurator
    {
        public void ConfigureServices(ClientConfiguration configuration, IServiceCollection services)
        {
            services.UseZooKeeperGatewayListProvider(options =>
            {
                options.ConnectionString = configuration.DataConnectionString;
            });
        }
    }
}
