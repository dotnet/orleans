using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.MembershipService;
using OrleansAWSUtils.Configuration;

namespace Microsoft.Orleans.Hosting
{
    public static class AwsUtilsServiceCollectionExtensions
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
    }
}
