using System;
using System.Linq;
using System.Net;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Messaging;
using Orleans.Runtime;

namespace Orleans
{
    /// <summary>
    /// Extension methods for <see cref="IClientBuilder"/>.
    /// </summary>
    public static class ClientBuilderExtensions
    {
        /// <summary>
        /// Configures default client services.
        /// </summary>
        /// <param name="builder">The host builder.</param>
        /// <returns>The client builder.</returns>
        public static IClientBuilder ConfigureDefaults(this IClientBuilder builder)
        {
            // Configure the container to use an Orleans client.
            builder.ConfigureServices(services =>
            {
                const string key = "OrleansClientServicesAdded";
                if (!builder.Properties.ContainsKey(key))
                {
                    DefaultClientServices.AddDefaultServices(services);
                    builder.Properties.Add(key, true);
                }
            });
            return builder;
        }
        /// <summary>
        /// Specify the environment to be used by the host.
        /// </summary>
        /// <param name="hostBuilder">The host builder to configure.</param>
        /// <param name="environment">The environment to host the application in.</param>
        /// <returns>The host builder.</returns>
        public static IClientBuilder UseEnvironment(this IClientBuilder hostBuilder, string environment)
        {
            return hostBuilder.ConfigureHostConfiguration(configBuilder =>
            {
                configBuilder.AddInMemoryCollection(new[]
                {
                    new KeyValuePair<string, string>(HostDefaults.EnvironmentKey,
                        environment  ?? throw new ArgumentNullException(nameof(environment)))
                });
            });
        }

        /// <summary>
        /// Adds services to the container. This can be called multiple times and the results will be additive.
        /// </summary>
        /// <param name="hostBuilder">The <see cref="IClientBuilder" /> to configure.</param>
        /// <param name="configureDelegate"></param>
        /// <returns>The same instance of the <see cref="IClientBuilder"/> for chaining.</returns>
        public static IClientBuilder ConfigureServices(this IClientBuilder hostBuilder, Action<IServiceCollection> configureDelegate)
        {
            return hostBuilder.ConfigureServices((context, collection) => configureDelegate(collection));
        }

        /// <summary>
        /// Sets up the configuration for the remainder of the build process and application. This can be called multiple times and
        /// the results will be additive. The results will be available at <see cref="HostBuilderContext.Configuration"/> for
        /// subsequent operations./>.
        /// </summary>
        /// <param name="hostBuilder">The host builder to configure.</param>
        /// <param name="configureDelegate"></param>
        /// <returns>The same instance of the host builder for chaining.</returns>
        public static IClientBuilder ConfigureAppConfiguration(this IClientBuilder hostBuilder, Action<IConfigurationBuilder> configureDelegate)
        {
            return hostBuilder.ConfigureAppConfiguration((context, builder) => configureDelegate(builder));
        }

        /// <summary>
        /// Registers an action used to configure a particular type of options.
        /// </summary>
        /// <typeparam name="TOptions">The options type to be configured.</typeparam>
        /// <param name="builder">The host builder.</param>
        /// <param name="configureOptions">The action used to configure the options.</param>
        /// <returns>The client builder.</returns>
        public static IClientBuilder Configure<TOptions>(this IClientBuilder builder, Action<TOptions> configureOptions) where TOptions : class
        {
            return builder.ConfigureServices(services => services.Configure(configureOptions));
        }

        /// <summary>
        /// Registers a configuration instance which <typeparamref name="TOptions"/> will bind against.
        /// </summary>
        /// <typeparam name="TOptions">The options type to be configured.</typeparam>
        /// <param name="builder">The host builder.</param>
        /// <param name="configuration">The configuration.</param>
        /// <returns>The client builder.</returns>
        public static IClientBuilder Configure<TOptions>(this IClientBuilder builder, IConfiguration configuration) where TOptions : class
        {
            return builder.ConfigureServices(services => services.AddOptions<TOptions>().Bind(configuration));
        }
        
        /// <summary>
        /// Registers a <see cref="GatewayCountChangedHandler"/> event handler.
        /// </summary>
        public static IClientBuilder AddGatewayCountChangedHandler(this IClientBuilder builder, GatewayCountChangedHandler handler)
        {
            builder.ConfigureServices(services => services.AddSingleton(handler));
            return builder;
        }

        /// <summary>
        /// Registers a <see cref="ConnectionToClusterLostHandler"/> event handler.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="handler">The handler.</param>
        /// <returns>The builder.</returns>
        public static IClientBuilder AddClusterConnectionLostHandler(this IClientBuilder builder, ConnectionToClusterLostHandler handler)
        {
            builder.ConfigureServices(services => services.AddSingleton(handler));
            return builder;
        }

        /// <summary>
        /// Specifies how the <see cref="IServiceProvider"/> for this client is configured. 
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="factory">The service provider factory.</param>
        /// <returns>The builder.</returns>
        public static IClientBuilder UseServiceProviderFactory(this IClientBuilder builder, Func<IServiceCollection, IServiceProvider> factory)
        {
            builder.UseServiceProviderFactory(new DelegateServiceProviderFactory(factory));
            return builder;
        }

        /// <summary>
        /// Adds a delegate for configuring the provided <see cref="ILoggingBuilder"/>. This may be called multiple times.
        /// </summary>
        /// <param name="builder">The <see cref="IClientBuilder" /> to configure.</param>
        /// <param name="configureLogging">The delegate that configures the <see cref="ILoggingBuilder"/>.</param>
        /// <returns>The same instance of the <see cref="IClientBuilder"/> for chaining.</returns>
        public static IClientBuilder ConfigureLogging(this IClientBuilder builder, Action<ILoggingBuilder> configureLogging)
        {
            return builder.ConfigureServices(collection => collection.AddLogging(loggingBuilder => configureLogging(loggingBuilder)));
        }

        /// <summary>
        /// Configures the client to connect to a silo on the localhost.
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="gatewayPort">The local silo's gateway port.</param>
        /// <param name="serviceId">The service id.</param>
        /// <param name="clusterId">The cluster id.</param>
        public static IClientBuilder UseLocalhostClustering(
            this IClientBuilder builder,
            int gatewayPort = 30000,
            string serviceId = ClusterOptions.DevelopmentServiceId,
            string clusterId = ClusterOptions.DevelopmentClusterId)
        {
            return builder.UseLocalhostClustering(new [] {gatewayPort}, serviceId, clusterId);
        }

        /// <summary>
        /// Configures the client to connect to a silo on the localhost.
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="gatewayPorts">The local silo gateway ports.</param>
        /// <param name="serviceId">The service id.</param>
        /// <param name="clusterId">The cluster id.</param>
        public static IClientBuilder UseLocalhostClustering(this IClientBuilder builder,
            int[] gatewayPorts,
            string serviceId = ClusterOptions.DevelopmentServiceId,
            string clusterId = ClusterOptions.DevelopmentClusterId)
        {
            return builder.UseStaticClustering(gatewayPorts.Select(p => new IPEndPoint(IPAddress.Loopback, p)).ToArray())
                .ConfigureServices(services =>
                {
                    // If the caller did not override service id or cluster id, configure default values as a fallback.
                    if (string.Equals(serviceId, ClusterOptions.DevelopmentServiceId) && string.Equals(clusterId, ClusterOptions.DevelopmentClusterId))
                    {
                        services.PostConfigure<ClusterOptions>(options =>
                        {
                            if (string.IsNullOrWhiteSpace(options.ClusterId)) options.ClusterId = ClusterOptions.DevelopmentClusterId;
                            if (string.IsNullOrWhiteSpace(options.ServiceId)) options.ServiceId = ClusterOptions.DevelopmentServiceId;
                        });
                    }
                    else
                    {
                        services.Configure<ClusterOptions>(options =>
                        {
                            options.ServiceId = serviceId;
                            options.ClusterId = clusterId;
                        });
                    }
                });
        }

        /// <summary>
        /// Configures the client to use static clustering.
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="endpoints">The gateway endpoints.</param>
        public static IClientBuilder UseStaticClustering(this IClientBuilder builder, params IPEndPoint[] endpoints)
        {
            return builder.UseStaticClustering(options => options.Gateways = endpoints.Select(ep => ep.ToGatewayUri()).ToList());
        }

        /// <summary>
        /// Configures the client to use static clustering.
        /// </summary>
        public static IClientBuilder UseStaticClustering(this IClientBuilder builder, Action<StaticGatewayListProviderOptions> configureOptions)
        {
            return builder.ConfigureServices(
                collection =>
                {
                    if (configureOptions != null)
                    {
                        collection.Configure(configureOptions);
                    }

                    collection.AddSingleton<IGatewayListProvider, StaticGatewayListProvider>()
                        .ConfigureFormatter<StaticGatewayListProviderOptions>();
                });
        }

        /// <summary>
        /// Configures the client to use static clustering.
        /// </summary>
        public static IClientBuilder UseStaticClustering(this IClientBuilder builder, Action<OptionsBuilder<StaticGatewayListProviderOptions>> configureOptions)
        {
            return builder.ConfigureServices(
                collection =>
                {
                    configureOptions?.Invoke(collection.AddOptions<StaticGatewayListProviderOptions>());
                    collection.AddSingleton<IGatewayListProvider, StaticGatewayListProvider>()
                        .ConfigureFormatter<StaticGatewayListProviderOptions>();
                });
        }
    }
}