using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Messaging;

namespace Orleans.AzureUtils.Storage
{
    /// <inheritdoc/>
    public class LegacyAzureTableGatewayListProviderConfigurator : ILegacyGatewayListProviderConfigurator
    {
        /// <inheritdoc/>
        public void ConfigureServices(object configuration, IServiceCollection services)
        {
            services.UseAzureTableGatewayListProvider(options =>
            {
                var reader = new ClientConfigurationReader(configuration);
                options.ConnectionString = reader.GetPropertyValue<string>("DataConnectionString");
            });
        }
    }
}
