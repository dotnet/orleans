using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.ConsulUtils.Configuration;
using Orleans.Messaging;
using Orleans.Runtime.Membership;
using OrleansConsulUtils.Options;

namespace Orleans.Hosting
{
    public static class ConsulUtilsHostingExtensions 
    {
        /// <summary>
        /// Configure siloHostBuilder to use ConsulMembership
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="configureOptions"></param>
        /// <returns></returns>
        public static ISiloHostBuilder UseConsulMembership(this ISiloHostBuilder builder,
            Action<ConsulMembershipOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.UseConsulMembership(configureOptions));
        }

        /// <summary>
        /// Configure siloHostBuilder to use ConsulMembership
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="configuration"></param>
        /// <returns></returns>
        public static ISiloHostBuilder UseConsulMembership(this ISiloHostBuilder builder,
            IConfiguration configuration)
        {
            return builder.ConfigureServices(services => services.UseConsulMembership(configuration));
        }

        /// <summary>
        /// Configure client to use ConsulGatewayListProvider
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="configureOptions"></param>
        /// <returns></returns>
        public static IClientBuilder UseConsulGatewayListProvider(this IClientBuilder builder,
            Action<ConsulGatewayListProviderOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.UseConsulGatewayListProvider(configureOptions));
        }

        /// <summary>
        /// Configure client to use ConsulGatewayListProvider
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="configuration"></param>
        /// <returns></returns>
        public static IClientBuilder UseConsulGatewayListProvider(this IClientBuilder builder,
            IConfiguration configuration)
        {
            return builder.ConfigureServices(services => services.UseConsulGatewayListProvider(configuration));
        }

        /// <summary>
        /// Configure DI container with ConsuleBasedMemebership
        /// </summary>
        /// <param name="services"></param>
        /// <param name="configureOptions"></param>
        /// <returns></returns>
        public static IServiceCollection UseConsulMembership(this IServiceCollection services,
            Action<ConsulMembershipOptions> configureOptions)
        {
            services.Configure<ConsulMembershipOptions>(configureOptions);
            services.AddSingleton<IMembershipTable, ConsulBasedMembershipTable>();
            return services;
        }

        /// <summary>
        /// Configure DI container with ConsuleBasedMemebership
        /// </summary>
        /// <param name="services"></param>
        /// <param name="configuration"></param>
        /// <returns></returns>
        public static IServiceCollection UseConsulMembership(this IServiceCollection services,
            IConfiguration configuration)
        {
            services.Configure<ConsulMembershipOptions>(configuration);
            services.AddSingleton<IMembershipTable, ConsulBasedMembershipTable>();
            return services;
        }

        /// <summary>
        /// Configure DI container with ConsulGatewayListProvider
        /// </summary>
        /// <param name="services"></param>
        /// <param name="configureOptions"></param>
        /// <returns></returns>
        public static IServiceCollection UseConsulGatewayListProvider(this IServiceCollection services,
            Action<ConsulGatewayListProviderOptions> configureOptions)
        {
            return services.Configure(configureOptions)
                .AddSingleton<IGatewayListProvider, ConsulGatewayListProvider>();
        }

        /// <summary>
        /// Configure DI container with ConsulGatewayProvider
        /// </summary>
        /// <param name="services"></param>
        /// <param name="configuration"></param>
        /// <returns></returns>
        public static IServiceCollection UseConsulGatewayListProvider(this IServiceCollection services,
            IConfiguration configuration)
        {
            return services.Configure<ConsulGatewayListProviderOptions>(configuration)
                .AddSingleton<IGatewayListProvider, ConsulGatewayListProvider>();
        }
    }
}
