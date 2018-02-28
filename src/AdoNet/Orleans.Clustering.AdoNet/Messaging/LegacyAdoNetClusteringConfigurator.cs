using Microsoft.Extensions.DependencyInjection;

using Orleans.Configuration;
using Orleans.Runtime.MembershipService;

namespace Orleans.AdoNet
{
    /// <inheritdoc />
    public class LegacyAdoNetClusteringConfigurator : ILegacyMembershipConfigurator
    {
        public void Configure(object configuration, IServiceCollection services)
        {
            services.Configure<AdoNetClusteringSiloOptions>(
                options =>
                {
                    var reader = new GlobalConfigurationReader(configuration);
                    options.Invariant = reader.GetPropertyValue<string>("AdoInvariant");
                    options.ConnectionString = reader.GetPropertyValue<string>("DataConnectionString");
                });
            services.AddSingleton<IMembershipTable, AdoNetClusteringTable>();
        }
    }
}
