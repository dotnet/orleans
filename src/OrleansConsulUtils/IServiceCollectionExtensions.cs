using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.ConsulUtils.Configuration;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Host;

namespace OrleansConsulUtils
{
    public static class IServiceCollectionExtensions
    {
        /// <summary>
        /// Configure DI container with ConsuleBasedMemebershipTable
        /// </summary>
        /// <param name="services"></param>
        /// <param name="configureOptions"></param>
        /// <returns></returns>
        public static IServiceCollection UseConsulMembershipTable(this IServiceCollection services,
            Action<ConsulMembershipTableOptions> configureOptions)
        {
            services.Configure<ConsulMembershipTableOptions>(configureOptions);
            services.AddSingleton<IMembershipTable, ConsulBasedMembershipTable>();
            return services;
        }

        /// <summary>
        /// Configure DI container with ConsuleBasedMemebershipTable
        /// </summary>
        /// <param name="services"></param>
        /// <param name="configuration"></param>
        /// <returns></returns>
        public static IServiceCollection UseConsulMembershipTable(this IServiceCollection services,
            IConfiguration configuration)
        {
            services.Configure<ConsulMembershipTableOptions>(configuration);
            services.AddSingleton<IMembershipTable, ConsulBasedMembershipTable>();
            return services;
        }

        /// <summary>
        /// Configure DI container with ConsulBasedMemebershipTable and get its configuration from <see cref="GlobalConfiguration"/>.
        /// </summary>
        /// <param name="services"></param>
        /// <returns></returns>
        public static IServiceCollection UseConsulMembershipTableFromLegacyConfigurationSupport(
            this IServiceCollection services)
        {
            services.Configure<GlobalConfiguration, ConsulMembershipTableOptions>((configuration, options) =>
            {
                options.DataConnectionString = configuration.DataConnectionString;
                options.DeploymentId = configuration.DeploymentId;
            });
            services.AddSingleton<IMembershipTable, ConsulBasedMembershipTable>();
            return services;
        }

        private static void Configure<TConfiguration, TOptions>(this IServiceCollection services, Action<TConfiguration, TOptions> configureOptions) where TOptions : class
        {
            services.AddSingleton<IConfigureOptions<TOptions>>(serviceProvider =>
            {
                var configuration = serviceProvider.GetRequiredService<TConfiguration>();

                return new ConfigureOptions<TOptions>(options => configureOptions(configuration, options));
            });
        }
    }
}
