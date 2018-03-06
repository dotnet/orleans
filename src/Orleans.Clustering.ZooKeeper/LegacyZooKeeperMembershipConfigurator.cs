using Orleans.Hosting;
using Orleans.Runtime.MembershipService;

namespace OrleansZooKeeperUtils
{
    /// <inheritdoc />
    public class LegacyZooKeeperMembershipConfigurator : ILegacyMembershipConfigurator
    {
        public void Configure(object configuration, ISiloHostBuilder builder)
        {
            var reader = new GlobalConfigurationReader(configuration);
            builder.UseZooKeeperClustering(options =>
            {
                options.ConnectionString = reader.GetPropertyValue<string>("DataConnectionString");
            });
        }
    }
}
