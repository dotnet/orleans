using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.MembershipService;
using OrleansAWSUtils.Configuration;

namespace OrleansAWSUtils
{
    public static class IServiceCollectionExtensions
    {
        /// <summary>
        /// Configure DI container with DynamoDBMembershipTable
        /// </summary>
        /// <param name="services"></param>
        /// <param name="configureOptions"></param>
        public static IServiceCollection UseDynamoDBMembershipTable(this IServiceCollection services,
            Action<DynamoDBMembershipTableOptions> configureOptions)
        {
            services.Configure<DynamoDBMembershipTableOptions>(configureOptions);
            services.AddSingleton<IMembershipTable, DynamoDBMembershipTable>();
            return services;
        }

        /// <summary>
        /// Configure DI container with DynamoDBMembershipTable
        /// </summary>
        public static IServiceCollection UseDynamoDBMembershipTable(this IServiceCollection services,
            IConfiguration config)
        {
            services.Configure<DynamoDBMembershipTableOptions>(config);
            services.AddSingleton<IMembershipTable, DynamoDBMembershipTable>();
            return services;
        }

        /// <summary>
        /// Configure DI container with DynamoDBMemebershipTable and get its configuration from <see cref="GlobalConfiguration"/>
        /// </summary>
        /// <param name="services"></param>
        /// <returns></returns>
        public static IServiceCollection UseDynamoDBMemebershipTableFromLegacyConfigurationSupport(
            this IServiceCollection services)
        {
            services.Configure<GlobalConfiguration, DynamoDBMembershipTableOptions>((configuration, options) =>
            {
                options.DataConnectionString = configuration.DataConnectionString;
                options.DeploymentId = configuration.DeploymentId;
            });
            services.AddSingleton<IMembershipTable, DynamoDBMembershipTable>();
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
