using System;
using Microsoft.Extensions.Configuration;
using Orleans.Hosting;
using OrleansAWSUtils.Configuration;

namespace OrleansAWSUtils
{
    public static class ISiloHostBuilderExtensions
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

        /// <summary>
        /// Configure DI to use DynamoDBMemebershipTable, and get its configuration from GlobalConfiguration.
        /// </summary>
        /// <param name="builder"></param>
        /// <returns></returns>
        public static ISiloHostBuilder UseDynamoDBMemebershipTableFromLegacyConfiguration(
            this ISiloHostBuilder builder)
        {
            builder.ConfigureServices(services => services.UseDynamoDBMemebershipTableFromLegacyConfigurationSupport());
            return builder;
        }
    }
}
