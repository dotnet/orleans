using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.MembershipService;
using OrleansAWSUtils.Configuration;

namespace Orleans.Hosting
{
    public static class AwsUtilsHostingExtensions
    {
        /// <summary>
        /// Configure SiloHostBuilder with DynamoDBMembership
        /// </summary>
        public static ISiloHostBuilder UseDynamoDBMembership(this ISiloHostBuilder builder,
            Action<DynamoDBMembershipOptions> configureOptions)
        {
            builder.ConfigureServices(services => services.UseDynamoDBMembership(configureOptions));
            return builder;
        }

        /// <summary>
        /// Configure SiloHostBuilder with DynamoDBMembership
        /// </summary>
        public static ISiloHostBuilder UseDynamoDBMembership(this ISiloHostBuilder builder,
            IConfiguration config)
        {
            builder.ConfigureServices(services => services.UseDynamoDBMembership(config));
            return builder;
        }

        /// <summary>
        /// Configure DI container with DynamoDBMembership
        /// </summary>
        /// <param name="services"></param>
        /// <param name="configureOptions"></param>
        public static IServiceCollection UseDynamoDBMembership(this IServiceCollection services,
            Action<DynamoDBMembershipOptions> configureOptions)
        {
            services.Configure<DynamoDBMembershipOptions>(configureOptions);
            services.AddSingleton<IMembershipTable, DynamoDBMembershipTable>();
            return services;
        }

        /// <summary>
        /// Configure DI container with DynamoDBMembership
        /// </summary>
        public static IServiceCollection UseDynamoDBMembership(this IServiceCollection services,
            IConfiguration config)
        {
            services.Configure<DynamoDBMembershipOptions>(config);
            services.AddSingleton<IMembershipTable, DynamoDBMembershipTable>();
            return services;
        }
    }
}
