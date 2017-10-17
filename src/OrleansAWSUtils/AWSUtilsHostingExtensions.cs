using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Messaging;
using Orleans.Runtime.Membership;
using Orleans.Runtime.MembershipService;
using OrleansAWSUtils.Configuration;
using OrleansAWSUtils.Options;

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
        /// Configure ClientBuilder with DynamoDBGatewayListProvider
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="configureOptions"></param>
        /// <returns></returns>
        public static IClientBuilder UseDynamoDBGatewayListProvider(this IClientBuilder builder,
            Action<DynamoDBGatewayListProviderOptions> configureOptions)
        {
            return  builder.ConfigureServices(services => services.UseDynamoDBGatewayListProvider(configureOptions));
        }

        /// <summary>
        /// Configure ClientBuilder with DynamoDBGatewayListProvider
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="configuration"></param>
        /// <returns></returns>
        public static IClientBuilder UseDynamoDBGatewayListProvider(this IClientBuilder builder,
            IConfiguration configuration)
        {
            return builder.ConfigureServices(services => services.UseDynamoDBGatewayListProvider(configuration));
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

        /// <summary>
        /// Condifure client with DynamoDBGatewayListProvider
        /// </summary>
        /// <param name="services"></param>
        /// <param name="configureOptions"></param>
        /// <returns></returns>
        public static IServiceCollection UseDynamoDBGatewayListProvider(this IServiceCollection services,
            Action<DynamoDBGatewayListProviderOptions> configureOptions)
        {
            return services.Configure<DynamoDBGatewayListProviderOptions>(configureOptions)
                .AddSingleton<IGatewayListProvider, DynamoDBGatewayListProvider>();
        }

        /// <summary>
        /// Condifure client with DynamoDBGatewayListProvider
        /// </summary>
        /// <param name="services"></param>
        /// <param name="configuration"></param>
        /// <returns></returns>
        public static IServiceCollection UseDynamoDBGatewayListProvider(this IServiceCollection services,
           IConfiguration configuration)
        {
            return services.Configure<DynamoDBGatewayListProviderOptions>(configuration)
                .AddSingleton<IGatewayListProvider, DynamoDBGatewayListProvider>();
        }
    }
}
