using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime.MembershipService
{
    /// <inheritdoc />
    public class LegacyDynamoDBMembershipConfigurator : ILegacyMembershipConfigurator
    {
        public void ConfigureServices(GlobalConfiguration configuration, IServiceCollection services)
        {
            services.UseDynamoDBMembership(options => options.ConnectionString = configuration.DataConnectionString);
        }
    }
}
