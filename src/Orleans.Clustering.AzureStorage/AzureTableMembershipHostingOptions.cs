using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.AzureUtils;
using Orleans.AzureUtils.Configuration;
using Orleans.Messaging;
using Orleans.Runtime.MembershipService;
using Orleans.AzureUtils.Options;

namespace Orleans.Hosting
{
    public static class AzureTableMembershipHostingOptions
    {
        /// <summary>
        /// Configure ISiloHostBuilder to use AzureTableBasedMembership
        /// </summary>
        public static ISiloHostBuilder UseAzureTableMembership(this ISiloHostBuilder builder,
            Action<AzureTableMembershipOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.UseAzureTableMembership(configureOptions));
        }

        /// <summary>
        /// Configure ISiloHostBuilder to use AzureTableBasedMembership
        /// </summary>
        public static ISiloHostBuilder UseAzureTableMembership(this ISiloHostBuilder builder,
            Action<OptionsBuilder<AzureTableMembershipOptions>> configureOptions)
        {
            return builder.ConfigureServices(services => services.UseAzureTableMembership(configureOptions));
        }

        /// Configure client to use AzureTableGatewayListProvider
        /// </summary>
        public static IClientBuilder UseAzureTableGatewayListProvider(this IClientBuilder builder,
            Action<AzureTableGatewayListProviderOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.UseAzureTableGatewayListProvider(configureOptions));
        }

        /// <summary>
        /// Configure client to use AzureTableGatewayListProvider
        /// </summary>
        public static IClientBuilder UseAzureTableGatewayListProvider(this IClientBuilder builder,
            Action<OptionsBuilder<AzureTableGatewayListProviderOptions>> configureOptions)
        {
            return builder.ConfigureServices(services => services.UseAzureTableGatewayListProvider(configureOptions));
        }

        /// <summary>
        /// Configure DI container to use AzureTableBasedMembership
        /// </summary>
        public static IServiceCollection UseAzureTableMembership(this IServiceCollection services,
            Action<AzureTableMembershipOptions> configureOptions)
        {
            return services.UseAzureTableMembership(ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure DI container to use AzureTableBasedMembership
        /// </summary>
        public static IServiceCollection UseAzureTableMembership(this IServiceCollection services,
            Action<OptionsBuilder<AzureTableMembershipOptions>> configureOptions)
        {
            configureOptions?.Invoke(services.AddOptions<AzureTableMembershipOptions>());
            services.AddSingleton<IMembershipTable, AzureBasedMembershipTable>();
            return services;
        }

        /// <summary>
        /// Configure DI container to use AzureTableGatewayListProvider
        /// </summary>
        public static IServiceCollection UseAzureTableGatewayListProvider(this IServiceCollection services,
            Action<AzureTableGatewayListProviderOptions> configureOptions)
        {
            return services.UseAzureTableGatewayListProvider(ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure DI container to use AzureTableGatewayListProvider
        /// </summary>
        public static IServiceCollection UseAzureTableGatewayListProvider(this IServiceCollection services,
            Action<OptionsBuilder<AzureTableGatewayListProviderOptions>> configureOptions)
        {
            configureOptions?.Invoke(services.AddOptions<AzureTableGatewayListProviderOptions>());
            return services.AddSingleton<IGatewayListProvider, AzureGatewayListProvider>();
        }
    }
}
