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
        /// Configure client to use ConsulGatewayProvider
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="configureOptions"></param>
        /// <returns></returns>
        public static IClientBuilder UseConsulGatewayProvider(this IClientBuilder builder,
            Action<ConsulGatewayProviderOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.UseConsulGatewayProvider(configureOptions));
        }

        /// <summary>
        /// Configure client to use ConsulGatewayProvider
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="configuration"></param>
        /// <returns></returns>
        public static IClientBuilder UseConsulGatewayProvider(this IClientBuilder builder,
            IConfiguration configuration)
        {
            return builder.ConfigureServices(services => services.UseConsulGatewayProvider(configuration));
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
        /// Configure DI container with ConsulBasedGatewayProvider
        /// </summary>
        /// <param name="services"></param>
        /// <param name="configureOptions"></param>
        /// <returns></returns>
        public static IServiceCollection UseConsulGatewayProvider(this IServiceCollection services,
            Action<ConsulGatewayProviderOptions> configureOptions)
        {
            return services.Configure(configureOptions)
                .AddSingleton<IGatewayListProvider, ConsulBasedGatewayListProvider>();
        }

        /// <summary>
        /// Configure DI container with ConsulBasedGatewayProvider
        /// </summary>
        /// <param name="services"></param>
        /// <param name="configuration"></param>
        /// <returns></returns>
        public static IServiceCollection UseConsulGatewayProvider(this IServiceCollection services,
            IConfiguration configuration)
        {
            return services.Configure<ConsulGatewayProviderOptions>(configuration)
                .AddSingleton<IGatewayListProvider, ConsulBasedGatewayListProvider>();
        }
    }
}
