using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.MembershipService;
using Orleans.Hosting;

namespace OrleansZooKeeperUtils
{
    /// <inheritdoc />
    public class LegacyZooKeeperMembershipConfigurator : ILegacyMembershipConfigurator
    {
        public void ConfigureServices(object configuration, IServiceCollection services)
        {
            var reader = new GlobalConfigurationReader(configuration);
            services.UseZooKeeperMembership(options => options.ConnectionString = reader.GetPropertyValue<string>("DataConnectionString"));
        }
    }
}
