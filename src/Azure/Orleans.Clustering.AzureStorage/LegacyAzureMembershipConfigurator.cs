using System;

using Microsoft.Extensions.DependencyInjection;

using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime.MembershipService
{
    /// <inheritdoc />
    public class LegacyAzureTableMembershipConfigurator : ILegacyMembershipConfigurator
    {
        public void Configure(object configuration, ISiloHostBuilder builder)
        {
            builder.UseAzureStorageClustering(options =>
            {
                var reader = new GlobalConfigurationReader(configuration);
                options.MaxStorageBusyRetries = reader.GetPropertyValue<int>("MaxStorageBusyRetries");
                options.ConnectionString = reader.GetPropertyValue<string>("DataConnectionString");
            });
        }
    }
}
