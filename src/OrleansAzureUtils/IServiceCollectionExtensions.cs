using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.AzureUtils.Configuration;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.MembershipService;

namespace OrleansAzureUtils
{
    public static class IServiceCollectionExtensions
    {
        /// <summary>
        /// Configure DI container to use AzureBasedMemebershipTable
        /// </summary>
        /// <param name="services"></param>
        /// <param name="configureOptions"></param>
        public static IServiceCollection UseAzureMemebershipTable(this IServiceCollection services,
            Action<AzureMembershipTableOptions> configureOptions)
        {
            services.Configure<AzureMembershipTableOptions>(configureOptions);
            services.AddSingleton<IMembershipTable, AzureBasedMembershipTable>();
            return services;
        }

        /// <summary>
        /// Configure DI container to use AzureBasedMemebershipTable
        /// </summary>
        /// <param name="services"></param>
        /// <param name="config"></param>
        public static IServiceCollection UseAzureMemebershipTable(this IServiceCollection services, IConfiguration config)
        {
            services.Configure<AzureMembershipTableOptions>(config);
            services.AddSingleton<IMembershipTable, AzureBasedMembershipTable>();
            return services;
        }


        /// <summary>
        /// Configure DI to use AzureBasedMemebershipTable, and get its configuration from <see cref="GlobalConfiguration"/>.
        /// </summary>
        /// <param name="services"></param>
        /// <returns></returns>
        public static IServiceCollection UseAzureMembershipTableFromLegacyConfigurationSupport(
            this IServiceCollection services)
        {
            services.Configure<GlobalConfiguration, AzureMembershipTableOptions>((configuration, options) =>
            {
                options.DataConnectionString = configuration.DataConnectionString;
                options.DeploymentId = configuration.DeploymentId;
                options.MaxStorageBusyRetries = configuration.MaxStorageBusyRetries;
            });
            services.AddSingleton<IMembershipTable, AzureBasedMembershipTable>();
            return services;
        }

        private static void Configure<TConfiguration, TOptions>(this IServiceCollection services,
            Action<TConfiguration, TOptions> configureOptions) where TOptions : class
        {
            services.AddSingleton<IConfigureOptions<TOptions>>(serviceProvider =>
            {
                var configuration = serviceProvider.GetRequiredService<TConfiguration>();

                return new ConfigureOptions<TOptions>(options => configureOptions(configuration, options));
            });
        }
    }
}
