using System;
using Orleans;
using Orleans.Runtime;

namespace Microsoft.Extensions.Hosting
{
    /// <summary>
    /// Extension methods for <see cref="IHostBuilder"/>.
    /// </summary>
    public static class GenericHostExtensions
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

            VerifyOrleansServerNotIncluded(hostBuilder);

            const string clientBuilderKey = "ClientBuilder";
            ClientBuilder clientBuilder;
            if (!hostBuilder.Properties.ContainsKey(clientBuilderKey))
            {
                clientBuilder = new ClientBuilder(hostBuilder);
                hostBuilder.Properties.Add(clientBuilderKey, clientBuilder);
            }
            else
            {
                clientBuilder = (ClientBuilder)hostBuilder.Properties[clientBuilderKey];
            }

            clientBuilder.ConfigureServices((ctx, _) => configureDelegate(ctx, clientBuilder));
            return hostBuilder;
        }

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
        public static IHostBuilder UseOrleansClient(this IHostBuilder hostBuilder, Action<IClientBuilder> configureDelegate)
        {
            if (hostBuilder == null) throw new ArgumentNullException(nameof(hostBuilder));
            if (configureDelegate == null) throw new ArgumentNullException(nameof(configureDelegate));
            return hostBuilder.UseOrleansClient((ctx, clientBuilder) => configureDelegate(clientBuilder));
        }

        private static void VerifyOrleansServerNotIncluded(IHostBuilder hostBuilder)
        {
            if (hostBuilder.Properties.ContainsKey("SiloBuilder"))
            {
                throw new OrleansConfigurationException("Do not use UseOrleans with UseOrleansClient. If you want a client and server in the same process, only UseOrleans is necessary and the UseOrleansClient call can be removed.");
            }
        }
    }
}
