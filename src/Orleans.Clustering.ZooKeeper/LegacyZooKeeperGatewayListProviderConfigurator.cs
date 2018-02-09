using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Messaging;

namespace OrleansZooKeeperUtils
{
    /// <inheritdoc/>
    public class LegacyZooKeeperGatewayListProviderConfigurator : ILegacyGatewayListProviderConfigurator
    {
        public void ConfigureServices(object configuration, IServiceCollection services)
        {
            services.UseZooKeeperGatewayListProvider(options =>
            {
                var reader = new ClientConfigurationReader(configuration);
                options.ConnectionString = reader.GetPropertyValue<string>("DataConnectionString");
            });
        }
    }
}
