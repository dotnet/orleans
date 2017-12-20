using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Messaging;
using Orleans.Runtime.Configuration;

namespace OrleansSQLUtils.Messaging
{
    /// <inheritdoc/>
    public class LegacySqlGatewayListProviderConfigurator : ILegacyGatewayListProviderConfigurator
    {
        public void ConfigureServices(ClientConfiguration configuration, IServiceCollection services)
        {
            services.UseSqlGatewayListProvider(options =>
            {
                options.ConnectionString = configuration.DataConnectionString;
                options.AdoInvariant = configuration.AdoInvariant;
            });
        }
    }
}
