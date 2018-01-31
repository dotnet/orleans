using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.MembershipService;
using Orleans.Hosting;

namespace OrleansSQLUtils
{
    /// <inheritdoc />
    public class LegacySqlMembershipConfigurator : ILegacyMembershipConfigurator
    {
        public void ConfigureServices(object configuration, IServiceCollection services)
        {
            services.UseSqlMembership(
                options =>
                {
                    var reader = new GlobalConfigurationReader(configuration);
                    options.AdoInvariant = reader.GetPropertyValue<string>("AdoInvariant");
                    options.ConnectionString = reader.GetPropertyValue<string>("DataConnectionString");
                });
        }
    }
}
