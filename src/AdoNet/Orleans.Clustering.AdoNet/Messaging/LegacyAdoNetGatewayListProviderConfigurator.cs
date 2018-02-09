using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Messaging;
using Orleans.Runtime.Configuration;

namespace Orleans.AdoNet.Messaging
{
    /// <inheritdoc/>
    public class LegacyAdoNetGatewayListProviderConfigurator : ILegacyGatewayListProviderConfigurator
    {
        public void ConfigureServices(ClientConfiguration configuration, IServiceCollection services)
        {
            services.UseAdoNetlGatewayListProvider(options =>
            {
                options.ConnectionString = configuration.DataConnectionString;
                options.AdoInvariant = configuration.AdoInvariant;
            });
        }
    }
}
