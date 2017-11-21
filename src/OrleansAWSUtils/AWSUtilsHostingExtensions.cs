using System;
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
            Action<OptionsBuilder<DynamoDBMembershipOptions>> configureOptions)
        {
            builder.ConfigureServices(services => services.UseDynamoDBMembership(configureOptions));
            return builder;
        }

        /// <summary>
        /// Configure ClientBuilder with DynamoDBGatewayListProvider
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="configureOptions"></param>
        /// <returns></returns>
        public static IClientBuilder UseDynamoDBGatewayListProvider(this IClientBuilder builder,
            Action<OptionsBuilder<DynamoDBGatewayListProviderOptions>> configureOptions)
        {
            return builder.ConfigureServices(services => services.UseDynamoDBGatewayListProvider(configureOptions));
        }

        /// <summary>
        /// Configure DI container with DynamoDBMembership
        /// </summary>
        /// <param name="services"></param>
        /// <param name="configureOptions"></param>
        public static IServiceCollection UseDynamoDBMembership(this IServiceCollection services,
            Action<OptionsBuilder<DynamoDBMembershipOptions>> configureOptions)
        {
            configureOptions?.Invoke(services.AddOptions<DynamoDBMembershipOptions>());
            return services.AddSingleton<IMembershipTable, DynamoDBMembershipTable>();
        }


        /// <summary>
        /// Condifure client with DynamoDBGatewayListProvider
        /// </summary>
        /// <param name="services"></param>
        /// <param name="configureOptions"></param>
        /// <returns></returns>
        public static IServiceCollection UseDynamoDBGatewayListProvider(this IServiceCollection services,
            Action<OptionsBuilder<DynamoDBGatewayListProviderOptions>> configureOptions)
        {
            configureOptions?.Invoke(services.AddOptions<DynamoDBGatewayListProviderOptions>());
            return services.AddSingleton<IGatewayListProvider, DynamoDBGatewayListProvider>();
        }
    }
}
