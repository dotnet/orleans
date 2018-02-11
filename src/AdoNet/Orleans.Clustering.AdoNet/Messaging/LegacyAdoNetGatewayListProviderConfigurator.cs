using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Messaging;

namespace Orleans.AdoNet.Messaging
{
    /// <inheritdoc/>
    public class LegacyAdoNetGatewayListProviderConfigurator : ILegacyGatewayListProviderConfigurator
    {
        public void ConfigureServices(object configuration, IServiceCollection services)
        {
            services.UseAdoNetlGatewayListProvider(options =>
            {
                var reader = new ClientConfigurationReader(configuration);
                options.ConnectionString = reader.GetPropertyValue<string>("DataConnectionString");
                options.AdoInvariant = reader.GetPropertyValue<string>("AdoInvariant");
            });
        }
    }
}
