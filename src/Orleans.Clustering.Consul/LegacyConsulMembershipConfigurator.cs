using System;
using Orleans.Hosting;

namespace Orleans.Runtime.MembershipService
{
    /// <inheritdoc />
    public class LegacyConsulMembershipConfigurator : ILegacyMembershipConfigurator
    {
        public void Configure(object configuration, ISiloHostBuilder builder)
        {
            var reader = new GlobalConfigurationReader(configuration);

            builder.UseConsulClustering(options =>
            {
                options.Address = new Uri(reader.GetPropertyValue<string>("DataConnectionString"));
            });
        }
    }
}
