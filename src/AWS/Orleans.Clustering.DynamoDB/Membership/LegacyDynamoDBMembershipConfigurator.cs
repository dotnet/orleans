using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime.MembershipService
{
    /// <inheritdoc />
    public class LegacyDynamoDBMembershipConfigurator : ILegacyMembershipConfigurator
    {
        public void ConfigureServices(object configuration, IServiceCollection services)
        {
            var reader = new GlobalConfigurationReader(configuration);
            services.UseDynamoDBMembership(options => options.ConnectionString = reader.GetPropertyValue<string>("DataConnectionString"));
        }
    }
}
