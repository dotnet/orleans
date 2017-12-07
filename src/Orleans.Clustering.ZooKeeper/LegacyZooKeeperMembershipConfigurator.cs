using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.MembershipService;
using Orleans.Hosting;

namespace OrleansZooKeeperUtils
{
    /// <inheritdoc />
    public class LegacyZooKeeperMembershipConfigurator : ILegacyMembershipConfigurator
    {
        public void ConfigureServices(GlobalConfiguration configuration, IServiceCollection services)
        {
            services.UseZooKeeperMembership(options => options.ConnectionString = configuration.DataConnectionString);
        }
    }
}
