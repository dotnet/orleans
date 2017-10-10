using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.AzureUtils.Configuration;
using Orleans.Runtime.MembershipService;

namespace Orleans.Hosting
{
    public static class AzureUtilsHostingExtensions
    {
        /// <summary>
        /// Configure ISiloHostBuilder to use AzureTableBasedMembership
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="configureOptions"></param>
        public static ISiloHostBuilder UseAzureTableMembership(this ISiloHostBuilder builder,
            Action<AzureTableMembershipOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.UseAzureTableMembership(configureOptions));
        }

        /// <summary>
        /// Configure ISiloHostBuilder to use AzureTableBasedMembership
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="config"></param>
        public static ISiloHostBuilder UseAzureTableMembership(this ISiloHostBuilder builder, IConfiguration config)
        {
            return builder.ConfigureServices(services => services.UseAzureTableMembership(config));
        }

        /// <summary>
        /// Configure DI container to use AzureTableBasedMembership
        /// </summary>
        /// <param name="services"></param>
        /// <param name="configureOptions"></param>
        public static IServiceCollection UseAzureTableMembership(this IServiceCollection services,
            Action<AzureTableMembershipOptions> configureOptions)
        {
            services.Configure<AzureTableMembershipOptions>(configureOptions);
            services.AddSingleton<IMembershipTable, AzureBasedMembershipTable>();
            return services;
        }

        /// <summary>
        /// Configure DI container to use AzureTableBasedMembership
        /// </summary>
        /// <param name="services"></param>
        /// <param name="config"></param>
        public static IServiceCollection UseAzureTableMembership(this IServiceCollection services, IConfiguration config)
        {
            services.Configure<AzureTableMembershipOptions>(config);
            services.AddSingleton<IMembershipTable, AzureBasedMembershipTable>();
            return services;
        }
    }
}
