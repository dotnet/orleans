using System;
using Microsoft.Extensions.Configuration;
using Orleans.Hosting;
using OrleansAWSUtils.Configuration;

namespace Microsoft.Orleans.Hosting
{
    public static class AwsUtilsSiloHostBuilderExtensions
    {
        /// <summary>
        /// Configure SiloHostBuilder with DynamoDBMembershipTable
        /// </summary>
        public static ISiloHostBuilder UseDynamoDBMembershipTable(this ISiloHostBuilder builder,
            Action<DynamoDBMembershipTableOptions> configureOptions)
        {
            builder.ConfigureServices(services => services.UseDynamoDBMembershipTable(configureOptions));
            return builder;
        }

        /// <summary>
        /// Configure SiloHostBuilder with DynamoDBMembershipTable
        /// </summary>
        public static ISiloHostBuilder UseDynamoDBMembershipTable(this ISiloHostBuilder builder,
             IConfiguration config)
        {
            builder.ConfigureServices(services => services.UseDynamoDBMembershipTable(config));
            return builder;
        }
    }
}
