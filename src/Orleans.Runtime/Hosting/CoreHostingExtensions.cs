using System;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.MembershipService;

namespace Orleans.Hosting
{
    /// <summary>
    /// Extensions for <see cref="ISiloHostBuilder"/> instances.
    /// </summary>
    public static class CoreHostingExtensions
    {
        /// <summary>
        /// Configure the container to use Orleans.
        /// </summary>
        /// <param name="builder">The host builder.</param>
        /// <returns>The host builder.</returns>
        public static ISiloHostBuilder ConfigureDefaults(this ISiloHostBuilder builder)
        {
            return builder.ConfigureServices((context, services) =>
            {
                if (!context.Properties.ContainsKey("OrleansServicesAdded"))
                {
                    services.PostConfigure<SiloOptions>(
                        options => options.SiloName =
                            options.SiloName
                            ?? context.HostingEnvironment.ApplicationName
                            ?? $"Silo_{Guid.NewGuid().ToString("N").Substring(0, 5)}");

                    services.TryAddSingleton<Silo>();
                    DefaultSiloServices.AddDefaultServices(context, services);

                    context.Properties.Add("OrleansServicesAdded", true);
                }
            });
        }

        /// <summary>
        /// Configure the container to use Orleans, including the default silo name & services.
        /// </summary>
        /// <param name="builder">The host builder.</param>
        /// <param name="configureOptions">The delegate that configures the options.</param>
        /// <returns>The host builder.</returns>
        public static ISiloHostBuilder Configure(this ISiloHostBuilder builder, Action<ClusterOptions> configureOptions)
        {
            return builder.Configure(null, configureOptions);
        }

        /// <summary>
        /// Configure the container to use Orleans, including the default silo name & services.
        /// </summary>
        /// <param name="builder">The host builder.</param>
        /// <param name="siloName">The name to use for this silo</param>
        /// <param name="configureOptions">The delegate that configures the options.</param>
        /// <returns>The host builder.</returns>
        public static ISiloHostBuilder Configure(this ISiloHostBuilder builder, string siloName, Action<ClusterOptions> configureOptions)
        {
            var optionsConfigurator = configureOptions != null
                ? ob => ob.Configure(configureOptions)
                : default(Action<OptionsBuilder<ClusterOptions>>);

            return builder.Configure(siloName, optionsConfigurator);
        }

        /// <summary>
        /// Configure the container to use Orleans, including the default silo name & services.
        /// </summary>
        /// <param name="builder">The host builder.</param>
        /// <param name="siloName">The name to use for this silo</param>
        /// <param name="configureOptions">The delegate that configures the options using the options builder.</param>
        /// <returns>The host builder.</returns>
        public static ISiloHostBuilder Configure(this ISiloHostBuilder builder, string siloName, Action<OptionsBuilder<ClusterOptions>> configureOptions = null)
        {
            return builder
                .ConfigureDefaults()
                .ConfigureServices((context, services) =>
                {
                    if (!string.IsNullOrEmpty(siloName))
                        builder.ConfigureSiloName(siloName);

                    configureOptions?.Invoke(services.AddOptions<ClusterOptions>());
                });
        }

        /// <summary>
        /// Configure the container to use Orleans, including the default silo name & services.
        /// </summary>
        /// <param name="builder">The host builder.</param>
        /// <param name="configureOptions">The delegate that configures the options using the options builder.</param>
        /// <returns>The host builder.</returns>
        public static ISiloHostBuilder Configure(this ISiloHostBuilder builder, Action<OptionsBuilder<ClusterOptions>> configureOptions = null)
        {
            return builder.Configure(null, configureOptions);
        }

        /// <summary>
        /// Configures the name of this silo.
        /// </summary>
        /// <param name="builder">The host builder.</param>
        /// <param name="siloName">The silo name.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloHostBuilder ConfigureSiloName(this ISiloHostBuilder builder, string siloName)
        {
            builder.Configure<SiloOptions>(options => options.SiloName = siloName);
            return builder;
        }

        /// <summary>
        /// Configures a localhost silo for development and testing.
        /// </summary>
        /// <param name="builder">The silo builder.</param>
        /// <param name="siloPort">The silo port.</param>
        /// <param name="gatewayPort">The gateway port.</param>
        /// <param name="primarySiloEndpoint">
        /// The endpoint of the primary silo, or <see langword="null"/> to use this silo as the primary.
        /// </param>
        /// <returns>The silo builder.</returns>
        public static ISiloHostBuilder UseLocalhostClustering(
            this ISiloHostBuilder builder,
            int siloPort = EndpointOptions.DEFAULT_SILO_PORT,
            int gatewayPort = EndpointOptions.DEFAULT_GATEWAY_PORT,
            IPEndPoint primarySiloEndpoint = null,
            string clusterId = ClusterOptions.DevelopmentClusterId)
        {
            builder.Configure<EndpointOptions>(options =>
            {
                options.AdvertisedIPAddress = IPAddress.Loopback;
                options.SiloPort = siloPort;
                options.GatewayPort = gatewayPort;
            });

            builder.UseDevelopmentClustering(primarySiloEndpoint ?? new IPEndPoint(IPAddress.Loopback, siloPort));

            if (!string.IsNullOrWhiteSpace(clusterId))
            {
                builder.Configure(options => options.ClusterId = clusterId);
            }

            return builder;
        }

        /// <summary>
        /// Configures the silo to use development-only clustering.
        /// </summary>
        /// <param name="primarySiloEndpoint">
        /// The endpoint of the primary silo, or <see langword="null"/> to use this silo as the primary.
        /// </param>
        /// <returns>The silo builder.</returns>
        public static ISiloHostBuilder UseDevelopmentClustering(this ISiloHostBuilder builder, IPEndPoint primarySiloEndpoint)
        {
            return builder.UseDevelopmentClustering(options => options.PrimarySiloEndpoint = primarySiloEndpoint);
        }

        /// <summary>
        /// Configures the silo to use development-only clustering.
        /// </summary>
        public static ISiloHostBuilder UseDevelopmentClustering(
            this ISiloHostBuilder builder,
            Action<DevelopmentMembershipOptions> configureOptions,
            string clusterId = ClusterOptions.DevelopmentClusterId)
        {
            return builder
                .Configure(options => options.ClusterId = clusterId)
                .ConfigureServices(
                services =>
                {
                    if (configureOptions != null)
                    {
                        services.Configure(configureOptions);
                    }

                    services
                        .AddSingleton<GrainBasedMembershipTable>()
                        .AddFromExisting<IMembershipTable, GrainBasedMembershipTable>();
                });
        }

        /// <summary>
        /// Configures the silo to use development-only clustering.
        /// </summary>
        public static ISiloHostBuilder UseDevelopmentClustering(
            this ISiloHostBuilder builder,
            Action<OptionsBuilder<DevelopmentMembershipOptions>> configureOptions,
            string clusterId = ClusterOptions.DevelopmentClusterId)
        {
            return builder.ConfigureServices(
                services =>
                {
                    configureOptions?.Invoke(services.AddOptions<DevelopmentMembershipOptions>());
                    services
                        .AddSingleton<GrainBasedMembershipTable>()
                        .AddFromExisting<IMembershipTable, GrainBasedMembershipTable>();
                });
        }
    }
}