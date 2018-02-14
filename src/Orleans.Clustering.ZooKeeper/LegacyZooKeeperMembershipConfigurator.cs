using Microsoft.Extensions.DependencyInjection;

using Orleans;
using Orleans.Configuration;
using Orleans.Runtime.MembershipService;
using Orleans.Runtime.Membership;

namespace OrleansZooKeeperUtils
{
    /// <inheritdoc />
    public class LegacyZooKeeperMembershipConfigurator : ILegacyMembershipConfigurator
    {
        public void Configure(object configuration, IServiceCollection services)
        {
            var reader = new GlobalConfigurationReader(configuration);
            services.Configure<ZooKeeperClusteringSiloOptions>(options => options.ConnectionString = reader.GetPropertyValue<string>("DataConnectionString"));
            services.AddSingleton<IMembershipTable, ZooKeeperBasedMembershipTable>();
        }
    }
}
