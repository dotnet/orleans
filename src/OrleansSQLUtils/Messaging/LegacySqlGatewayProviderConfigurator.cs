using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Messaging;
using Orleans.Runtime.Configuration;

namespace OrleansSQLUtils.Messaging
{
    public class LegacySqlGatewayProviderConfigurator : ILegacyGatewayListProviderConfigurator
    {
        public void ConfigureServices(ClientConfiguration configuration, IServiceCollection services)
        {
            services.UseSqlGatewayProvider(options =>
            {
                options.ConnectionString = configuration.DataConnectionString;
                options.AdoInvariant = configuration.AdoInvariant;
            });
        }
    }
}
