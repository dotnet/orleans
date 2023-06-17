using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Runtime;

namespace Microsoft.Extensions.Hosting
{
    /// <summary>
    /// Extension methods for <see cref="IHostBuilder"/>.
    /// </summary>
    public static class OrleansSiloGenericHostExtensions
    {
        private static readonly Type MarkerType = typeof(OrleansBuilderMarker);

        /// <summary>
        /// Configures the host app builder to host an Orleans silo.
        /// </summary>
        /// <param name="hostAppBuilder">The host app builder.</param>
        /// <returns>The host builder.</returns>
        public static HostApplicationBuilder UseOrleans(
            this HostApplicationBuilder hostAppBuilder) =>
            hostAppBuilder.UseOrleans(_ => { });

        /// <summary>
        /// Configures the host builder to host an Orleans silo.
        /// </summary>
        /// <param name="hostAppBuilder">The host app builder.</param>
        /// <param name="configureDelegate">The delegate used to configure the silo.</param>
        /// <returns>The host builder.</returns>
        /// <remarks>
        /// Calling this method multiple times on the same <see cref="HostApplicationBuilder"/> instance will result in one silo being configured.
        /// However, the effects of <paramref name="configureDelegate"/> will be applied once for each call.
        /// </remarks>
        public static HostApplicationBuilder UseOrleans(
            this HostApplicationBuilder hostAppBuilder,
            Action<ISiloBuilder> configureDelegate)
        {
            ArgumentNullException.ThrowIfNull(hostAppBuilder);
            ArgumentNullException.ThrowIfNull(configureDelegate);

            hostAppBuilder.Services.AddOrleans(configureDelegate);

            return hostAppBuilder;
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
        public static IHostBuilder UseOrleans(
            this IHostBuilder hostBuilder,
            Action<ISiloBuilder> configureDelegate) => hostBuilder.UseOrleans((_, siloBuilder) => configureDelegate(siloBuilder));

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
            ArgumentNullException.ThrowIfNull(hostBuilder);
            ArgumentNullException.ThrowIfNull(configureDelegate);

            if (hostBuilder.Properties.ContainsKey("HasOrleansClientBuilder"))
            {
                throw GetOrleansClientAddedException();
            }

            hostBuilder.Properties["HasOrleansSiloBuilder"] = "true";

            return hostBuilder.ConfigureServices((context, services) => configureDelegate(context, AddOrleans(services)));
        }

        /// <summary>
        /// Configures the service collection to host an Orleans silo.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configureDelegate">The delegate used to configure the silo.</param>
        /// <returns>The service collection.</returns>
        /// <remarks>
        /// Calling this method multiple times on the same <see cref="IHostBuilder"/> instance will result in one silo being configured.
        /// However, the effects of <paramref name="configureDelegate"/> will be applied once for each call.
        /// </remarks>
        public static IServiceCollection AddOrleans(
            this IServiceCollection services,
            Action<ISiloBuilder> configureDelegate)
        {
            ArgumentNullException.ThrowIfNull(configureDelegate);

            var builder = AddOrleans(services);

            configureDelegate(builder);

            return services;
        }

        private static ISiloBuilder AddOrleans(IServiceCollection services)
        {
            ISiloBuilder builder = default;
            foreach (var descriptor in services.Where(d => d.ServiceType.Equals(MarkerType)))
            {
                var marker = (OrleansBuilderMarker)descriptor.ImplementationInstance;
                builder = marker.BuilderInstance switch
                {

                    ISiloBuilder existingBuilder => existingBuilder,
                    _ => throw GetOrleansClientAddedException()
                };
            }

            if (builder is null)
            {
                builder = new SiloBuilder(services);
                services.AddSingleton(new OrleansBuilderMarker(builder));
            }

            return builder;
        }

        private static OrleansConfigurationException GetOrleansClientAddedException() =>
            new("Do not call both UseOrleansClient/AddOrleansClient with UseOrleans/AddOrleans. If you want a client and server in the same process, only UseOrleans/AddOrleans is necessary and the UseOrleansClient/AddOrleansClient call can be removed.");
    }
}