using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Messaging;
using Orleans.Runtime.Configuration;

namespace Orleans.AzureUtils.Storage
{
    /// <inheritdoc/>
    public class LegacyAzureTableGatewayListProviderConfigurator : ILegacyGatewayListProviderConfigurator
    {
        /// <inheritdoc/>
        public void ConfigureServices(ClientConfiguration configuration, IServiceCollection services)
        {
            services.UseAzureTableGatewayListProvider(options =>
            {
                options.ConnectionString = configuration.DataConnectionString;
            });
        }
    }
}
