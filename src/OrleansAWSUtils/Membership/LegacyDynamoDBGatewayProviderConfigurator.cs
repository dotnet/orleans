using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Messaging;
using Orleans.Runtime.Configuration;

namespace OrleansAWSUtils.Membership
{
    /// <inheritdoc cref="ILegacyGatewayListProviderConfigurator"/>
    public class LegacyDynamoDBGatewayProviderConfigurator : ILegacyGatewayListProviderConfigurator
    {
        /// <inheritdoc cref="ILegacyGatewayListProviderConfigurator"/>
        public void ConfigureServices(ClientConfiguration configuration, IServiceCollection services)
        {
            services.UseDynamoDBGatewayProvider(options =>
            {
                options.ConnectionString = configuration.DataConnectionString;
            });
        }
    }
}
