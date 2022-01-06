using System;
using System.Linq;
using System.Net;
using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Messaging;
using Orleans.Runtime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Orleans
{
    /// <summary>
    /// Extension methods for <see cref="IClientBuilder"/>.
    /// </summary>
    public static class ClientBuilderExtensions
    {
        /// <summary>
        /// Configures the provided delegate as a connection retry filter, used to determine whether initial connection to the Orleans cluster should be retried after a failure.
        /// </summary>
        /// <param name="builder">The host builder.</param>
        /// <param name="connectionRetryFilter">The connection retry filter.</param>
        /// <returns>The same instance of the <see cref="IClientBuilder"/> for chaining.</returns>
        public static IClientBuilder UseConnectionRetryFilter(this IClientBuilder builder, Func<Exception, CancellationToken, Task<bool>> connectionRetryFilter)
        {
            return builder.ConfigureServices(collection => collection.AddSingleton<IClientConnectionRetryFilter>(new DelegateConnectionRetryFilter(connectionRetryFilter)));
        }

        /// <summary>
        /// Configures the provided delegate as a connection retry filter, used to determine whether initial connection to the Orleans cluster should be retried after a failure.
        /// </summary>
        /// <param name="builder">The host builder.</param>
        /// <param name="connectionRetryFilter">The connection retry filter.</param>
        /// <returns>The same instance of the <see cref="IClientBuilder"/> for chaining.</returns>
        public static IClientBuilder UseConnectionRetryFilter(this IClientBuilder builder, IClientConnectionRetryFilter connectionRetryFilter)
        {
            return builder.ConfigureServices(collection => collection.AddSingleton<IClientConnectionRetryFilter>(connectionRetryFilter));
        }

        /// <summary>
        /// Configures the provided <typeparamref name="TConnectionRetryFilter"/> type as a connection retry filter, used to determine whether initial connection to the Orleans cluster should be retried after a failure.
        /// </summary>
        /// <param name="builder">The host builder.</param>
        /// <returns>The same instance of the <see cref="IClientBuilder"/> for chaining.</returns>
        public static IClientBuilder UseConnectionRetryFilter<TConnectionRetryFilter>(this IClientBuilder builder) where TConnectionRetryFilter : class, IClientConnectionRetryFilter
        {
            return builder.ConfigureServices(collection => collection.AddSingleton<IClientConnectionRetryFilter, TConnectionRetryFilter>());
        }

        private sealed class DelegateConnectionRetryFilter : IClientConnectionRetryFilter
        {
            private readonly Func<Exception, CancellationToken, Task<bool>> _filter;
            public DelegateConnectionRetryFilter(Func<Exception, CancellationToken, Task<bool>> connectionRetryFilter) => _filter = connectionRetryFilter ?? throw new ArgumentNullException(nameof(connectionRetryFilter));
            public Task<bool> ShouldRetryConnectionAttempt(Exception exception, CancellationToken cancellationToken) => _filter(exception, cancellationToken);
        }

        /// <summary>
        /// Adds services to the container. This can be called multiple times and the results will be additive.
        /// </summary>
        /// <param name="builder">The host builder.</param>
        /// <param name="configureDelegate"></param>
        /// <returns>The same instance of the <see cref="IClientBuilder"/> for chaining.</returns>
        public static IClientBuilder ConfigureServices(this IClientBuilder builder, Action<IServiceCollection> configureDelegate)
        {
            if (configureDelegate == null) throw new ArgumentNullException(nameof(configureDelegate));
            configureDelegate(builder.Services);
            return builder;
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
        /// Add <see cref="Activity.Current"/> propagation through grain calls.
        /// Note: according to <see cref="ActivitySource.StartActivity(string, ActivityKind)"/> activity will be created only when any listener for activity exists <see cref="ActivitySource.HasListeners()"/> and <see cref="ActivityListener.Sample"/> returns <see cref="ActivitySamplingResult.PropagationData"/>.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <returns>The builder.</returns>
        public static IClientBuilder AddActivityPropagation(this IClientBuilder builder)
        {
            builder.Services.TryAddSingleton(DistributedContextPropagator.Current);

            return builder
                .AddOutgoingGrainCallFilter<ActivityPropagationOutgoingGrainCallFilter>();
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