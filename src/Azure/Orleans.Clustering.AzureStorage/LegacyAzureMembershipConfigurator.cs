using Microsoft.Extensions.DependencyInjection;
using Orleans.Clustering.AzureStorage;
using Orleans.Hosting;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime.MembershipService
{
    /// <inheritdoc />
    public class LegacyAzureTableMembershipConfigurator : ILegacyMembershipConfigurator
    {
        public void ConfigureServices(object configuration, IServiceCollection services)
        {
            services.UseAzureTableMembership(
                options =>
                {
                    var reader = new GlobalConfigurationReader(configuration);
                    options.MaxStorageBusyRetries = reader.GetPropertyValue<int>("MaxStorageBusyRetries");
                    options.ConnectionString = reader.GetPropertyValue<string>("DataConnectionString");
                });

            services.AddTransient<IConfigurationValidator>(sp => new AzureTableMembershipConfigurationValidator(configuration));

        }
    }
}
