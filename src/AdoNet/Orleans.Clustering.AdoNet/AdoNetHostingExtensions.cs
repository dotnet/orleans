using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Messaging;
using Orleans.Runtime.Membership;
using Orleans.Runtime.MembershipService;
using Orleans.Configuration;

namespace Orleans.Hosting
{
    public static class AdoNetHostingExtensions
    {
        /// <summary>
        /// Configure SiloHostBuilder with ADO.NET clustering
        /// </summary>
        public static ISiloHostBuilder UseAdoNetClustering(this ISiloHostBuilder builder,
            Action<AdoNetClusteringOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.UseAdoNetClustering(configureOptions));
        }

        /// <summary>
        /// Configure SiloHostBuilder with ADO.NET clustering
        /// </summary>
        public static ISiloHostBuilder UseAdoNetClustering(this ISiloHostBuilder builder,
            Action<OptionsBuilder<AdoNetClusteringOptions>> configureOptions)
        {
            return builder.ConfigureServices(services => services.UseAdoNetClustering(configureOptions));
        }

        /// <summary>
        /// Configure client to use ADO.NET gateway list provider
        /// </summary>
        public static IClientBuilder UseAdoNetlGatewayListProvider(this IClientBuilder builder, Action<AdoNetGatewayListProviderOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.UseAdoNetlGatewayListProvider(configureOptions));
        }

        /// <summary>
        /// Configure client to use ADO.NET gateway list provider
        /// </summary>
        public static IClientBuilder UseAdoNetlGatewayListProvider(this IClientBuilder builder, Action<OptionsBuilder<AdoNetGatewayListProviderOptions>> configureOptions)
        {
            return builder.ConfigureServices(services => services.UseAdoNetlGatewayListProvider(configureOptions));
        }

        /// <summary>
        /// Configure DI container with ADO.NET clustering
        /// </summary>
        public static IServiceCollection UseAdoNetClustering(this IServiceCollection services,
            Action<AdoNetClusteringOptions> configureOptions)
        {
            return services.UseAdoNetClustering(ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure DI container with ADO.NET clustering
        /// </summary>
        public static IServiceCollection UseAdoNetClustering(this IServiceCollection services,
            Action<OptionsBuilder<AdoNetClusteringOptions>> configureOptions)
        {
            configureOptions?.Invoke(services.AddOptions<AdoNetClusteringOptions>());
            return services.AddSingleton<IMembershipTable, AdoNetClusteringTable>();
        }

        /// <summary>
        /// Configure DI container with ADO.NET gateway list provider
        /// </summary>
        public static IServiceCollection UseAdoNetlGatewayListProvider(this IServiceCollection services,
            Action<AdoNetGatewayListProviderOptions> configureOptions)
        {
            return services.UseAdoNetlGatewayListProvider(ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure DI container with ADO.NET gateway list provider
        /// </summary>
        public static IServiceCollection UseAdoNetlGatewayListProvider(this IServiceCollection services,
            Action<OptionsBuilder<AdoNetGatewayListProviderOptions>> configureOptions)
        {
            configureOptions?.Invoke(services.AddOptions<AdoNetGatewayListProviderOptions>());
            return services.AddSingleton<IGatewayListProvider, AdoNetGatewayListProvider>();
        }
    }
}
