using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Hosting;
using Orleans.Runtime;

namespace Microsoft.Extensions.Hosting
{
    /// <summary>
    /// Extension methods for <see cref="IHostBuilder"/>.
    /// </summary>
    public static class OrleansClientGenericHostExtensions
    {
        /// <summary>
        /// Configures the host builder to host an Orleans client.
        /// </summary>
        /// <param name="hostBuilder">The host builder.</param>
        /// <param name="configureDelegate">The delegate used to configure the client.</param>
        /// <returns>The host builder.</returns>
        /// <remarks>
        /// Calling this method multiple times on the same <see cref="IClientBuilder"/> instance will result in one client being configured.
        /// However, the effects of <paramref name="configureDelegate"/> will be applied once for each call.
        /// Note that this method should not be used in conjunction with IHostBuilder.UseOrleans, since UseOrleans includes a client automatically.
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="hostBuilder"/> was null or <paramref name="configureDelegate"/> was null.</exception>
        public static IHostBuilder UseOrleansClient(this IHostBuilder hostBuilder, Action<HostBuilderContext, IClientBuilder> configureDelegate)
        {
            if (hostBuilder == null) throw new ArgumentNullException(nameof(hostBuilder));
            if (configureDelegate == null) throw new ArgumentNullException(nameof(configureDelegate));
            if (hostBuilder.Properties.ContainsKey("HasOrleansSiloBuilder"))
            {
                throw GetOrleansSiloAddedException();
            }

            hostBuilder.Properties["HasOrleansClientBuilder"] = "true";
            return hostBuilder.ConfigureServices((ctx, services) => configureDelegate(ctx, AddOrleansClient(services)));
        }

        /// <summary>
        /// Configures the service collection to host an Orleans client.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configureDelegate">The delegate used to configure the client.</param>
        /// <returns>The service collection.</returns>
        /// <remarks>
        /// Calling this method multiple times on the same <see cref="IClientBuilder"/> instance will result in one client being configured.
        /// However, the effects of <paramref name="configureDelegate"/> will be applied once for each call.
        /// Note that this method should not be used in conjunction with UseOrleans, since UseOrleans includes a client automatically.
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="services"/> was null or <paramref name="configureDelegate"/> was null.</exception>
        public static IServiceCollection AddOrleansClient(this IServiceCollection services, Action<IClientBuilder> configureDelegate)
        {
            if (configureDelegate == null) throw new ArgumentNullException(nameof(configureDelegate));
            var clientBuilder = AddOrleansClient(services);
            configureDelegate(clientBuilder);
            return services;
        }

        private static IClientBuilder AddOrleansClient(IServiceCollection services)
        {
            IClientBuilder clientBuilder = default;
            foreach (var descriptor in services)
            {
                if (descriptor.ServiceType.Equals(typeof(OrleansBuilderMarker)))
                {
                    var instance = (OrleansBuilderMarker)descriptor.ImplementationInstance;
                    clientBuilder = instance.Instance switch
                    {
                        IClientBuilder existingBuilder => existingBuilder,
                        _ => throw GetOrleansSiloAddedException()
                    };
                }
            }

            if (clientBuilder is null)
            {
                clientBuilder = new ClientBuilder(services);
                services.Add(new(typeof(OrleansBuilderMarker), new OrleansBuilderMarker(clientBuilder)));
            }

            return clientBuilder;
        }

        private static OrleansConfigurationException GetOrleansSiloAddedException() => new("Do not use UseOrleans with UseOrleansClient. If you want a client and server in the same process, only UseOrleans is necessary and the UseOrleansClient call can be removed.");

        /// <summary>
        /// Marker type used for storing a client builder in a service collection.
        /// </summary>
        internal class OrleansBuilderMarker
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="OrleansBuilderMarker"/> class.
            /// </summary>
            /// <param name="builderInstance">The builder instance.</param>
            public OrleansBuilderMarker(object builderInstance) => Instance = builderInstance;

            /// <summary>
            /// Gets the builder instance.
            /// </summary>
            public object Instance { get; }
        }
    }
}
