using Microsoft.Extensions.DependencyInjection;

using Orleans.Configuration;
using Orleans.Messaging;
using Orleans.Runtime.Membership;

namespace Orleans.AdoNet.Messaging
{
    /// <inheritdoc/>
    public class LegacyAdoNetGatewayListProviderConfigurator : ILegacyGatewayListProviderConfigurator
    {
        public void ConfigureServices(object configuration, IServiceCollection services)
        {
            services.Configure<AdoNetClusteringClientOptions>(
                options =>
                {
                    var reader = new ClientConfigurationReader(configuration);
                    options.ConnectionString = reader.GetPropertyValue<string>("DataConnectionString");
                    options.Invariant = reader.GetPropertyValue<string>("AdoInvariant");
                });
            services.AddSingleton<IGatewayListProvider, AdoNetGatewayListProvider>();
        }
    }
}
