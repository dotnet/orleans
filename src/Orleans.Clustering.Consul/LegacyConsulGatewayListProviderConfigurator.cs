using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Messaging;
using Orleans.Runtime.Configuration;

namespace Orleans.ConsulUtils
{
    /// <inheritdoc/>
    public class LegacyConsulGatewayListProviderConfigurator : ILegacyGatewayListProviderConfigurator
    {
        /// <inheritdoc/>
        public void ConfigureServices(ClientConfiguration configuration, IServiceCollection services)
        {
            services.UseConsulGatewayListProvider(options =>
            {
                options.Address = new Uri(configuration.DataConnectionString);
            });
        }
    }
}
