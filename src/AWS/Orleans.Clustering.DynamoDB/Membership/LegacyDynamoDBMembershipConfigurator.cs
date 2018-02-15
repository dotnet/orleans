using Microsoft.Extensions.DependencyInjection;

using Orleans.Configuration;

namespace Orleans.Runtime.MembershipService
{
    /// <inheritdoc />
    public class LegacyDynamoDBMembershipConfigurator : ILegacyMembershipConfigurator
    {
        public void Configure(object configuration, IServiceCollection services)
        {
            var reader = new GlobalConfigurationReader(configuration);

            services.Configure<DynamoDBClusteringSiloOptions>(options => options.ConnectionString = reader.GetPropertyValue<string>("DataConnectionString"));
            services.AddSingleton<IMembershipTable, DynamoDBMembershipTable>();
        }
    }
}
