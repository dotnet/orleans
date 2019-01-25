using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Orleans.Hosting
{
    /// <summary>
    /// Extension methods for <see cref="IHostBuilder"/>.
    /// </summary>
    public static class GenericHostExtensions
    {
        /// <summary>
        /// Configures the host builder to host an Orleans silo.
        /// </summary>
        /// <param name="hostBuilder">The host builder.</param>
        /// <param name="configureDelegate">The delegate used to configure the silo.</param>
        /// <returns>The host builder.</returns>
        /// <remarks>
        /// Calling this method multiple times on the same <see cref="IHostBuilder"/> instance will result in one silo being configured.
        /// However, the effects of <paramref name="configureDelegate"/> will be applied once for each call.
        /// </remarks>
        public static IHostBuilder UseOrleans(this IHostBuilder hostBuilder, Action<ISiloBuilder> configureDelegate)
        {
            if (configureDelegate == null) throw new ArgumentNullException(nameof(configureDelegate));

            const string siloBuilderKey = "SiloBuilder";
            SiloBuilder siloBuilder;
            if (!hostBuilder.Properties.ContainsKey(siloBuilderKey))
            {
                siloBuilder = new SiloBuilder(hostBuilder);
                hostBuilder.Properties.Add(siloBuilderKey, siloBuilder);
                siloBuilder
                    .ConfigureDefaults()
                    .ConfigureServices((context, services) => services.AddHostedService<SiloHostedService>());

                // Allow the user to override default behaviors and add application parts.
                configureDelegate.Invoke(siloBuilder);

                siloBuilder.ConfigureApplicationParts(parts => parts.ConfigureDefaults());
            }
            else
            {
                siloBuilder = (SiloBuilder)hostBuilder.Properties[siloBuilderKey];
                configureDelegate.Invoke(siloBuilder);
            }

            return hostBuilder;
        }
    }
}