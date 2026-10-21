using System;
using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Configuration.Internal;
using Orleans.GrainDirectory;
using Orleans.Runtime;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Hosting;
using Orleans.Runtime.MembershipService;

namespace Orleans.Hosting
{
    /// <summary>
    /// Extensions for <see cref="ISiloBuilder"/> instances.
    /// </summary>
    public static class CoreHostingExtensions
    {
        private static readonly ServiceDescriptor DirectoryDescriptor = ServiceDescriptor.Singleton<DistributedGrainDirectory, DistributedGrainDirectory>();
        private static readonly ServiceDescriptor DirectoryLifecycleDescriptor =
            ServiceDescriptor.Singleton<ILifecycleParticipant<ISiloLifecycle>>(static sp => sp.GetRequiredService<DistributedGrainDirectory>());
        private static readonly ServiceDescriptor DirectoryMembershipDescriptor = ServiceDescriptor.Singleton(static sp =>
        {
            var options = sp.GetRequiredService<IOptions<GrainDirectoryOptions>>().Value;
            return new DirectoryMembershipService(
                sp.GetRequiredService<ClusterMembershipService>(),
                sp.GetRequiredService<IInternalGrainFactory>(),
                sp.GetRequiredService<ILogger<DirectoryMembershipService>>(),
                options.PartitionsPerSilo,
                options.GetPartitionBoundaries);
        });

        /// <summary>
        /// Add <see cref="Activity.Current"/> propagation through grain calls.
        /// Note: according to <see cref="ActivitySource.StartActivity(string, ActivityKind)"/> activity will be created only when any listener for activity exists <see cref="ActivitySource.HasListeners()"/> and <see cref="ActivityListener.Sample"/> returns <see cref="ActivitySamplingResult.PropagationData"/>.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <returns>The builder.</returns>
        public static ISiloBuilder AddActivityPropagation(this ISiloBuilder builder)
        {
            builder.Services.TryAddSingleton(DistributedContextPropagator.Current);

            return builder
                .AddOutgoingGrainCallFilter<ActivityPropagationOutgoingGrainCallFilter>()
                .AddIncomingGrainCallFilter<ActivityPropagationIncomingGrainCallFilter>();
        }

        /// <summary>
        /// Configures the silo to use development-only clustering and listen on localhost.
        /// </summary>
        /// <param name="builder">The silo builder.</param>
        /// <param name="siloPort">The silo port.</param>
        /// <param name="gatewayPort">The gateway port.</param>
        /// <param name="primarySiloEndpoint">
        /// The endpoint of the primary silo, or <see langword="null"/> to use this silo as the primary.
        /// </param>
        /// <param name="serviceId">The service id.</param>
        /// <param name="clusterId">The cluster id.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloBuilder UseLocalhostClustering(
            this ISiloBuilder builder,
            int siloPort = EndpointOptions.DEFAULT_SILO_PORT,
            int gatewayPort = EndpointOptions.DEFAULT_GATEWAY_PORT,
            IPEndPoint? primarySiloEndpoint = null,
            string serviceId = ClusterOptions.DevelopmentServiceId,
            string clusterId = ClusterOptions.DevelopmentClusterId)
        {
            builder.Configure<EndpointOptions>(options =>
            {
                options.AdvertisedIPAddress = IPAddress.Loopback;
                options.SiloPort = siloPort;
                options.GatewayPort = gatewayPort;
            });

            builder.UseDevelopmentClustering(optionsBuilder => ConfigurePrimarySiloEndpoint(optionsBuilder, primarySiloEndpoint));
            builder.ConfigureServices(services =>
            {
                // If the caller did not override service id or cluster id, configure default values as a fallback.
                if (string.Equals(serviceId, ClusterOptions.DevelopmentServiceId, StringComparison.Ordinal) && string.Equals(clusterId, ClusterOptions.DevelopmentClusterId, StringComparison.Ordinal))
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
        public static ISiloBuilder UseDevelopmentClustering(this ISiloBuilder builder, IPEndPoint? primarySiloEndpoint)
        {
            return builder.UseDevelopmentClustering(optionsBuilder => ConfigurePrimarySiloEndpoint(optionsBuilder, primarySiloEndpoint));
        }

        /// <summary>
        /// Configures the silo to use development-only clustering.
        /// </summary>
        public static ISiloBuilder UseDevelopmentClustering(
            this ISiloBuilder builder,
            Action<DevelopmentClusterMembershipOptions> configureOptions)
        {
            return builder.UseDevelopmentClustering(options => options.Configure(configureOptions));
        }

        /// <summary>
        /// Configures the silo to use development-only clustering.
        /// </summary>
        public static ISiloBuilder UseDevelopmentClustering(
            this ISiloBuilder builder,
            Action<OptionsBuilder<DevelopmentClusterMembershipOptions>> configureOptions)
        {
            return builder.ConfigureServices(
                services =>
                {
                    configureOptions?.Invoke(services.AddOptions<DevelopmentClusterMembershipOptions>());
                    services.ConfigureFormatter<DevelopmentClusterMembershipOptions>();
                    services
                        .AddSingleton<SystemTargetBasedMembershipTable>()
                        .AddFromExisting<IMembershipTable, SystemTargetBasedMembershipTable>();
                    services
                        .AddSingleton<MembershipTableSystemTarget>()
                        .AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, MembershipTableSystemTarget>();
                });
        }

        private static void ConfigurePrimarySiloEndpoint(OptionsBuilder<DevelopmentClusterMembershipOptions> optionsBuilder, IPEndPoint? primarySiloEndpoint)
        {
            optionsBuilder.Configure((DevelopmentClusterMembershipOptions options, IOptions<EndpointOptions> endpointOptions) =>
            {
                if (primarySiloEndpoint is null)
                {
                    primarySiloEndpoint = endpointOptions.Value.GetPublicSiloEndpoint();
                }

                options.PrimarySiloEndpoint = primarySiloEndpoint;
            });
        }

        /// <summary>
        /// Selects the legacy local grain directory as the default for grain types without an explicit directory selection.
        /// </summary>
        /// <param name="siloBuilder">The silo builder.</param>
        /// <returns>The provided silo builder.</returns>
        /// <remarks>
        /// Configure every silo with this option to defer migration to the distributed grain directory.
        /// Migration from the local directory to the distributed directory is one-way.
        /// </remarks>
        public static ISiloBuilder UseLocalGrainDirectory(this ISiloBuilder siloBuilder)
        {
            siloBuilder.Services.RemoveDefaultGrainDirectory();
            return siloBuilder;
        }

        internal static void RemoveUnusedDistributedGrainDirectory(IServiceCollection services)
        {
            foreach (var service in services)
            {
                if (!service.IsKeyedService
                    && service.ImplementationInstance is NamedService<IGrainDirectory> registration
                    && registration.ImplementationType == typeof(DistributedGrainDirectory))
                {
                    return;
                }
            }

            // Removing unused distributed services lets the local directory install its compatibility endpoints.
            services.Remove(DirectoryDescriptor);
            services.Remove(DirectoryLifecycleDescriptor);
            services.Remove(DirectoryMembershipDescriptor);
        }

        /// <summary>
        /// Configures the distributed grain directory.
        /// </summary>
        /// <param name="siloBuilder">The silo builder to register the directory implementation with.</param>
        /// <param name="name">The name to register the directory under, or null to register it as the default. Multiple names share one directory instance.</param>
        /// <returns>The provided silo builder.</returns>
        public static ISiloBuilder AddDistributedGrainDirectory(this ISiloBuilder siloBuilder, string? name = null)
        {
            var services = siloBuilder.Services;
            if (string.IsNullOrEmpty(name))
            {
                name = GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY;
            }

            if (name == GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY)
            {
                services.RemoveDefaultGrainDirectory();
            }

            // Distributed Grain Directory
            services.AddOptions<GrainDirectoryOptions>();
            services.TryAdd(DirectoryMembershipDescriptor);
            if (!services.Contains(DirectoryDescriptor))
            {
                services.Add(DirectoryDescriptor);
                services.Add(DirectoryLifecycleDescriptor);
            }

            services.AddGrainDirectoryAlias<DistributedGrainDirectory>(
                name,
                static (sp, _) => sp.GetRequiredService<DistributedGrainDirectory>());
            return siloBuilder;
        }
    }
}
