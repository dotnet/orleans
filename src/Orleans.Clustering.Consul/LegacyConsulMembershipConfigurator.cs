using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;

namespace Orleans.Runtime.MembershipService
{
    /// <inheritdoc />
    public class LegacyConsulMembershipConfigurator : ILegacyMembershipConfigurator
    {
        public void ConfigureServices(object configuration, IServiceCollection services)
        {
            var reader = new GlobalConfigurationReader(configuration);
            services.UseConsulMembership(options => options.Address = new Uri(reader.GetPropertyValue<string>("DataConnectionString")));
        }
    }
}
