﻿using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.AzureUtils;
using Orleans.AzureUtils.Configuration;
using Orleans.Messaging;
using Orleans.Runtime.MembershipService;
using OrleansAzureUtils.Options;

namespace Orleans.Hosting
{
    public static class AzureUtilsHostingExtensions
    {
        /// <summary>
        /// Configure ISiloHostBuilder to use AzureTableBasedMembership
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="configureOptions"></param>
        public static ISiloHostBuilder UseAzureTableMembership(this ISiloHostBuilder builder,
            Action<AzureTableMembershipOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.UseAzureTableMembership(configureOptions));
        }

        /// <summary>
        /// Configure ISiloHostBuilder to use AzureTableBasedMembership
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="config"></param>
        public static ISiloHostBuilder UseAzureTableMembership(this ISiloHostBuilder builder, IConfiguration config)
        {
            return builder.ConfigureServices(services => services.UseAzureTableMembership(config));
        }

        /// <summary>
        /// Configure client to use AzureTableGatewayProvider
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="configureOptions"></param>
        /// <returns></returns>
        public static IClientBuilder UseAzureTableGatewayProvider(this IClientBuilder builder,
            Action<AzureTableGatewayProviderOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.UseAzureTableGatewayProvider(configureOptions));
        }

        /// <summary>
        /// Configure client to use AzureTableGatewayProvider
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="configuration"></param>
        /// <returns></returns>
        public static IClientBuilder UseAzureTableGatewayProvider(this IClientBuilder builder,
            IConfiguration configuration)
        {
            return builder.ConfigureServices(services => services.UseAzureTableGatewayProvider(configuration));
        }

        /// <summary>
        /// Configure DI container to use AzureTableBasedMembership
        /// </summary>
        /// <param name="services"></param>
        /// <param name="configureOptions"></param>
        public static IServiceCollection UseAzureTableMembership(this IServiceCollection services,
            Action<AzureTableMembershipOptions> configureOptions)
        {
            services.Configure<AzureTableMembershipOptions>(configureOptions);
            services.AddSingleton<IMembershipTable, AzureBasedMembershipTable>();
            return services;
        }

        /// <summary>
        /// Configure DI container to use AzureTableBasedMembership
        /// </summary>
        /// <param name="services"></param>
        /// <param name="config"></param>
        public static IServiceCollection UseAzureTableMembership(this IServiceCollection services, IConfiguration config)
        {
            services.Configure<AzureTableMembershipOptions>(config);
            services.AddSingleton<IMembershipTable, AzureBasedMembershipTable>();
            return services;
        }

        /// <summary>
        /// Configure DI container to use AzureTableGatewayProvider
        /// </summary>
        /// <param name="services"></param>
        /// <param name="configureOptions"></param>
        /// <returns></returns>
        public static IServiceCollection UseAzureTableGatewayProvider(this IServiceCollection services,
            Action<AzureTableGatewayProviderOptions> configureOptions)
        {
            return services.Configure(configureOptions)
                .AddSingleton<IGatewayListProvider, AzureGatewayListProvider>();
        }

        /// <summary>
        /// Configure DI container to use AzureTableGatewayProvider
        /// </summary>
        /// <param name="services"></param>
        /// <param name="configuration"></param>
        /// <returns></returns>
        public static IServiceCollection UseAzureTableGatewayProvider(this IServiceCollection services,
            IConfiguration configuration)
        {
            return services.Configure<AzureTableGatewayProviderOptions>(configuration)
                .AddSingleton<IGatewayListProvider, AzureGatewayListProvider>();
        }
    }
}
