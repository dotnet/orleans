using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.ConsulUtils.Configuration;
using Orleans.Runtime.Membership;

namespace Orleans.Hosting
{
    public static class ConsulUtilsHostingExtensions 
    {
        /// <summary>
        /// Configure siloHostBuilder to use ConsulMembership
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="configureOptions"></param>
        /// <returns></returns>
        public static ISiloHostBuilder UseConsulMembership(this ISiloHostBuilder builder,
            Action<ConsulMembershipOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.UseConsulMembership(configureOptions));
        }

        /// <summary>
        /// Configure siloHostBuilder to use ConsulMembership
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="configuration"></param>
        /// <returns></returns>
        public static ISiloHostBuilder UseConsulMembership(this ISiloHostBuilder builder,
            IConfiguration configuration)
        {
            return builder.ConfigureServices(services => services.UseConsulMembership(configuration));
        }

        /// <summary>
        /// Configure DI container with ConsuleBasedMemebership
        /// </summary>
        /// <param name="services"></param>
        /// <param name="configureOptions"></param>
        /// <returns></returns>
        public static IServiceCollection UseConsulMembership(this IServiceCollection services,
            Action<ConsulMembershipOptions> configureOptions)
        {
            services.Configure<ConsulMembershipOptions>(configureOptions);
            services.AddSingleton<IMembershipTable, ConsulBasedMembershipTable>();
            return services;
        }

        /// <summary>
        /// Configure DI container with ConsuleBasedMemebership
        /// </summary>
        /// <param name="services"></param>
        /// <param name="configuration"></param>
        /// <returns></returns>
        public static IServiceCollection UseConsulMembership(this IServiceCollection services,
            IConfiguration configuration)
        {
            services.Configure<ConsulMembershipOptions>(configuration);
            services.AddSingleton<IMembershipTable, ConsulBasedMembershipTable>();
            return services;
        }
    }
}
