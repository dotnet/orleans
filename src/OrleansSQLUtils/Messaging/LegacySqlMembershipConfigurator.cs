using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.MembershipService;
using Orleans.Hosting;

namespace OrleansSQLUtils
{
    /// <inheritdoc />
    public class LegacySqlMembershipConfigurator : ILegacyMembershipConfigurator
    {
        public void ConfigureServices(GlobalConfiguration configuration, IServiceCollection services)
        {
            services.UseSqlMembership(
                options =>
                {
                    options.AdoInvariant = configuration.AdoInvariant;
                    options.ConnectionString = configuration.DataConnectionString;
                });
        }
    }
}
