using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime.MembershipService
{
    /// <inheritdoc />
    public class LegacyConsulMembershipConfigurator : ILegacyMembershipConfigurator
    {
        public void ConfigureServices(GlobalConfiguration configuration, IServiceCollection services)
        {
            services.UseConsulMembership(options => options.Address = new Uri(configuration.DataConnectionString));
        }
    }
}
