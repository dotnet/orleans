using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.MembershipService;
using Orleans.Hosting;

namespace Orleans.AdoNet
{
    /// <inheritdoc />
    public class LegacyAdoNetClusteringConfigurator : ILegacyMembershipConfigurator
    {
        public void ConfigureServices(object configuration, IServiceCollection services)
        {
            services.UseAdoNetClustering(
                options =>
                {
                    var reader = new GlobalConfigurationReader(configuration);
                    options.AdoInvariant = reader.GetPropertyValue<string>("AdoInvariant");
                    options.ConnectionString = reader.GetPropertyValue<string>("DataConnectionString");
                });
        }
    }
}
