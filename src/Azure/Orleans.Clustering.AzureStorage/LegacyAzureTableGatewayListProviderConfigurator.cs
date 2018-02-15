using Microsoft.Extensions.DependencyInjection;

using Orleans.Configuration;
using Orleans.Messaging;

namespace Orleans.AzureUtils.Storage
{
    /// <inheritdoc/>
    public class LegacyAzureTableGatewayListProviderConfigurator : ILegacyGatewayListProviderConfigurator
    {
        /// <inheritdoc/>
        public void ConfigureServices(object configuration, IServiceCollection services)
        {
            services.Configure<AzureStorageGatewayOptions>(
                options =>
                {
                    var reader = new ClientConfigurationReader(configuration);
                    options.ConnectionString = reader.GetPropertyValue<string>("DataConnectionString");
                });
            services.AddSingleton<IGatewayListProvider, AzureGatewayListProvider>();
        }
    }
}
