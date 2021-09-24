using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;

namespace Microsoft.Extensions.Hosting
{
    public static class GenericHostExtensions
    {
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
                hostBuilder.ConfigureServices((context, services) =>
                {
                    clientBuilder.Build(context, services);
                });
            }
            else
            {
                clientBuilder = (ClientBuilder)hostBuilder.Properties[clientBuilderKey];
            }

            clientBuilder.ConfigureClient(configureDelegate);
            return hostBuilder;
        }

        /// <summary>
        /// Configures the host builder to host an Orleans silo.
        /// </summary>
        /// <param name="hostBuilder">The host builder.</param>
        /// <param name="configureDelegate">The delegate used to configure the silo.</param>
        /// <returns>The host builder.</returns>
        /// <remarks>
        /// Calling this method multiple times on the same <see cref="IClientBuilder"/> instance will result in one silo being configured.
        /// However, the effects of <paramref name="configureDelegate"/> will be applied once for each call.
        /// </remarks>
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
                throw new OrleansConfigurationException("Do not use UseOrleans with UseOrleansClient");
            }
        }
    }
}
