#nullable enable
using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
        public static ISiloBuilder UseDevelopmentClustering(this ISiloBuilder builder, IPEndPoint primarySiloEndpoint)
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
        /// Opts-in to the experimental distributed grain directory.
        /// </summary>
        /// <param name="siloBuilder">The silo builder to register the directory implementation with.</param>
        /// <param name="name">The name of the directory to register, or null to register the directory as the default.</param>
        /// <returns>The provided silo builder.</returns>
        [Experimental("ORLEANSEXP003")]
        public static ISiloBuilder AddDistributedGrainDirectory(this ISiloBuilder siloBuilder, string? name = null)
        {
            var services = siloBuilder.Services;
            if (string.IsNullOrEmpty(name))
            {
                name = GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY;
            }

            // Distributed Grain Directory
            services.TryAddSingleton<DirectoryMembershipService>();
            if (!services.Contains(DirectoryDescriptor))
            {
                services.Add(DirectoryDescriptor);
                services.AddGrainDirectory<DistributedGrainDirectory>(name, (sp, name) => sp.GetRequiredService<DistributedGrainDirectory>());
            }

            return siloBuilder;
        }
    }
}
