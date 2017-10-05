using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.MembershipService;
using OrleansAWSUtils.Configuration;

namespace Orleans.Runtime.Hosting
{
    public static class AwsUtilsServiceCollectionExtensions
    {
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
