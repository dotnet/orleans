using System;
using Orleans.Hosting;

namespace Microsoft.Extensions.Hosting
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
        public static IHostBuilder UseOrleans(
            this IHostBuilder hostBuilder,
            Action<HostBuilderContext, ISiloBuilder> configureDelegate)
        {
            if (configureDelegate == null) throw new ArgumentNullException(nameof(configureDelegate));

            const string siloBuilderKey = "SiloBuilder";
            SiloBuilder siloBuilder;
            if (!hostBuilder.Properties.ContainsKey(siloBuilderKey))
            {
                siloBuilder = new SiloBuilder(hostBuilder);
                hostBuilder.Properties.Add(siloBuilderKey, siloBuilder);
                hostBuilder.ConfigureServices((context, services) =>
                {
                    siloBuilder.Build(context, services);
                });
            }
            else
            {
                siloBuilder = (SiloBuilder)hostBuilder.Properties[siloBuilderKey];
            }

            siloBuilder.ConfigureSilo(configureDelegate);
            return hostBuilder;
        }

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
            return hostBuilder.UseOrleans((ctx, siloBuilder) => configureDelegate(siloBuilder));
        }
    }
}