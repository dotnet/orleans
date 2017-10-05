using System;
using Microsoft.Extensions.Configuration;
using Orleans.Hosting;
using OrleansAWSUtils.Configuration;

namespace Orleans.Runtime.Hosting
{
    public static class AwsUtilsSiloHostBuilderExtensions
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
    }
}
