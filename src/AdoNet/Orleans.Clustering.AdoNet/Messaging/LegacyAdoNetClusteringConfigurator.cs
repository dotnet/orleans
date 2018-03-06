using Orleans.Hosting;
using Orleans.Runtime.MembershipService;

namespace Orleans.AdoNet
{
    /// <inheritdoc />
    public class LegacyAdoNetClusteringConfigurator : ILegacyMembershipConfigurator
    {
        public void Configure(object configuration, ISiloHostBuilder builder)
        {
            builder.UseAdoNetClustering(options =>
            {
                var reader = new GlobalConfigurationReader(configuration);
                options.Invariant = reader.GetPropertyValue<string>("AdoInvariant");
                options.ConnectionString = reader.GetPropertyValue<string>("DataConnectionString");
            });
        }
    }
}
