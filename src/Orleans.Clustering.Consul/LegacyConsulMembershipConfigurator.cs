using System;
using Microsoft.Extensions.DependencyInjection;

using Orleans.Configuration;
using Orleans.Runtime.Membership;

namespace Orleans.Runtime.MembershipService
{
    /// <inheritdoc />
    public class LegacyConsulMembershipConfigurator : ILegacyMembershipConfigurator
    {
        public void Configure(object configuration, IServiceCollection services)
        {
            var reader = new GlobalConfigurationReader(configuration);

            services.Configure<ConsulClusteringSiloOptions>(options => options.Address = new Uri(reader.GetPropertyValue<string>("DataConnectionString")));
            services.AddSingleton<IMembershipTable, ConsulBasedMembershipTable>();
        }
    }
}
