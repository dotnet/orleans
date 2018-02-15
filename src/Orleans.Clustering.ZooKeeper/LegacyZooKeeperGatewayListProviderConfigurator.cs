using Microsoft.Extensions.DependencyInjection;

using Orleans.Configuration;
using Orleans.Messaging;
using Orleans.Runtime.Membership;

namespace OrleansZooKeeperUtils
{
    /// <inheritdoc/>
    public class LegacyZooKeeperGatewayListProviderConfigurator : ILegacyGatewayListProviderConfigurator
    {
        public void ConfigureServices(object configuration, IServiceCollection services)
        {
            services.Configure<ZooKeeperGatewayListProviderOptions>(
                options =>
                {
                    var reader = new ClientConfigurationReader(configuration);
                    options.ConnectionString = reader.GetPropertyValue<string>("DataConnectionString");
                });
            services.AddSingleton<IGatewayListProvider, ZooKeeperClusteringClientOptions>();
        }
    }
}
