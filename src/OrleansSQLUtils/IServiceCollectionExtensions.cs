using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.MembershipService;
using OrleansSQLUtils.Configuration;

namespace OrleansSQLUtils
{
    public static class IServiceCollectionExtensions
    {
        /// <summary>
        /// Configure DI container with SqlMemebershipTable
        /// </summary>
        /// <param name="services"></param>
        /// <param name="configureOptions"></param>
        /// <returns></returns>
        public static IServiceCollection UseSqlMembershipTable(this IServiceCollection services,
            Action<SqlMembershipTableOptions> configureOptions)
        {
            return services.Configure<SqlMembershipTableOptions>(configureOptions)
                .AddSingleton<IMembershipTable, SqlMembershipTable>();
        }

        /// <summary>
        /// Configure DI container with SqlMemebershipTable
        /// </summary>
        /// <param name="services"></param>
        /// <param name="configuration"></param>
        /// <returns></returns>
        public static IServiceCollection UseSqlMembershipTable(this IServiceCollection services,
            IConfiguration configuration)
        {
            return services.Configure<SqlMembershipTableOptions>(configuration)
           .AddSingleton<IMembershipTable, SqlMembershipTable>();
        }

        /// <summary>
        /// Configure DI container with SqlMembershipTable, and gets its configuration from <see cref="GlobalConfiguration"/>
        /// </summary>
        /// <param name="services"></param>
        /// <returns></returns>
        public static IServiceCollection UseSqlMembershipFromLegacyConfigurationSupport(
            this IServiceCollection services)
        {
            services.Configure<GlobalConfiguration, SqlMembershipTableOptions>((configuration, options) =>
            {
                options.DataConnectionString = configuration.DataConnectionString;
                options.DeploymentId = configuration.DeploymentId;
                options.AdoInvariant = configuration.AdoInvariant;
            });
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
