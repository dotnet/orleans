using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Messaging;
using Orleans.Runtime.Configuration;

namespace OrleansAzureUtils.Storage
{
    /// <inheritdoc/>
    public class LegacyAzureTableGatewayListProviderConfigurator : ILegacyGatewayListProviderConfigurator
    {
        /// <inheritdoc/>
        public void ConfigureServices(ClientConfiguration configuration, IServiceCollection services)
        {
            services.UseAzureTableGatewayListProvider(ob => ob.Configure(options => options.ConnectionString = configuration.DataConnectionString));
        }
    }
}
