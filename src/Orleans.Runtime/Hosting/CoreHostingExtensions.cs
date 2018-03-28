using System;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Configuration;
using Orleans.Runtime;
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
        /// Configures a localhost silo for development and testing.
        /// </summary>
        /// <param name="builder">The silo builder.</param>
        /// <param name="siloPort">The silo port.</param>
        /// <param name="gatewayPort">The gateway port.</param>
        /// <param name="primarySiloEndpoint">
        /// The endpoint of the primary silo, or <see langword="null"/> to use this silo as the primary.
        /// </param>
        /// <param name="clusterId">Cluster ID</param>
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
            builder.Configure<ClusterOptions>(options => options.ClusterId = clusterId);
            builder.Configure<ClusterMembershipOptions>(options => options.ExpectedClusterSize = 1);

            return builder;
        }

        /// <summary>
        /// Configures the silo to use development-only clustering.
        /// </summary>
        /// <param name="builder"></param>
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
            Action<DevelopmentClusterMembershipOptions> configureOptions,
            string clusterId = ClusterOptions.DevelopmentClusterId)
        {
            return builder
                .Configure<ClusterOptions>(options => options.ClusterId = clusterId)
                .ConfigureServices(
                services =>
                {
                    if (configureOptions != null)
                    {
                        services.Configure(configureOptions);
                    }

                    services.ConfigureFormatter<DevelopmentClusterMembershipOptions>();
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
            Action<OptionsBuilder<DevelopmentClusterMembershipOptions>> configureOptions,
            string clusterId = ClusterOptions.DevelopmentClusterId)
        {
            return builder.ConfigureServices(
                services =>
                {
                    configureOptions?.Invoke(services.AddOptions<DevelopmentClusterMembershipOptions>());
                    services.ConfigureFormatter<DevelopmentClusterMembershipOptions>();
                    services
                        .AddSingleton<GrainBasedMembershipTable>()
                        .AddFromExisting<IMembershipTable, GrainBasedMembershipTable>();
                });
        }
    }
}