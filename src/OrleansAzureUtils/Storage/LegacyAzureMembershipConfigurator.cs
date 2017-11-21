using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime.MembershipService
{
    /// <inheritdoc />
    public class LegacyAzureTableMembershipConfigurator : ILegacyMembershipConfigurator
    {
        public void ConfigureServices(GlobalConfiguration configuration, IServiceCollection services)
        {
            services.UseAzureTableMembership(
                ob => ob.Configure(
                    options =>
                    {
                        options.MaxStorageBusyRetries = configuration.MaxStorageBusyRetries;
                        options.ConnectionString = configuration.DataConnectionString;
                    }));
        }
    }
}
