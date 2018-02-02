using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.ConsulUtils.Configuration;
using Orleans.Messaging;
using Orleans.Runtime.Membership;
using Orleans.ConsulUtils.Options;

namespace Orleans.Hosting
{
    public static class ConsulUtilsHostingExtensions 
    {
        /// <summary>
        /// Configure siloHostBuilder to use ConsulMembership
        /// </summary>
        public static ISiloHostBuilder UseConsulMembership(this ISiloHostBuilder builder,
            Action<ConsulMembershipOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.UseConsulMembership(configureOptions));
        }

        /// <summary>
        /// Configure siloHostBuilder to use ConsulMembership
        /// </summary>
        public static ISiloHostBuilder UseConsulMembership(this ISiloHostBuilder builder,
            Action<OptionsBuilder<ConsulMembershipOptions>> configureOptions)
        {
            return builder.ConfigureServices(services => services.UseConsulMembership(configureOptions));
        }

        /// <summary>
        /// Configure client to use ConsulGatewayListProvider
        /// </summary>
        public static IClientBuilder UseConsulGatewayListProvider(this IClientBuilder builder,
            Action<ConsulGatewayListProviderOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.UseConsulGatewayListProvider(configureOptions));
        }

        /// <summary>
        /// Configure client to use ConsulGatewayListProvider
        /// </summary>
        public static IClientBuilder UseConsulGatewayListProvider(this IClientBuilder builder,
            Action<OptionsBuilder<ConsulGatewayListProviderOptions>> configureOptions)
        {
            return builder.ConfigureServices(services => services.UseConsulGatewayListProvider(configureOptions));
        }

        /// <summary>
        /// Configure DI container with Consul based Membership
        /// </summary>
        public static IServiceCollection UseConsulMembership(this IServiceCollection services,
            Action<ConsulMembershipOptions> configureOptions)
        {
            return services.UseConsulMembership(ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure DI container with Consul based Membership
        /// </summary>
        public static IServiceCollection UseConsulMembership(this IServiceCollection services,
            Action<OptionsBuilder<ConsulMembershipOptions>> configureOptions)
        {
            configureOptions?.Invoke(services.AddOptions<ConsulMembershipOptions>());
            services.AddSingleton<IMembershipTable, ConsulBasedMembershipTable>();
            return services;
        }

        /// <summary>
        /// Configure DI container with ConsulGatewayListProvider
        /// </summary>
        public static IServiceCollection UseConsulGatewayListProvider(this IServiceCollection services,
            Action<ConsulGatewayListProviderOptions> configureOptions)
        {
            return services.UseConsulGatewayListProvider(ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure DI container with ConsulGatewayProvider
        /// </summary>
        public static IServiceCollection UseConsulGatewayListProvider(this IServiceCollection services,
            Action<OptionsBuilder<ConsulGatewayListProviderOptions>> configureOptions)
        {
            configureOptions?.Invoke(services.AddOptions<ConsulGatewayListProviderOptions>());
            return services.AddSingleton<IGatewayListProvider, ConsulGatewayListProvider>();
        }
    }
}
