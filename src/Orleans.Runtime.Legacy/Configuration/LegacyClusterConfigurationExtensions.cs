using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.MembershipService;
using Orleans.Runtime.Scheduler;
using Orleans.Providers;
using Orleans.Configuration.Options;
using System.Collections.Generic;

namespace Orleans.Hosting
{
    public static class LegacyClusterConfigurationExtensions
    {
        private const int SiloDefaultProviderInitStage = SiloLifecycleStage.RuntimeStorageServices;
        private const int SiloDefaultProviderStartStage = SiloLifecycleStage.ApplicationServices;

        /// <summary>
        /// Specifies the configuration to use for this silo.
        /// </summary>
        /// <param name="builder">The host builder.</param>
        /// <param name="configuration">The configuration.</param>
        /// <remarks>This method may only be called once per builder instance.</remarks>
        /// <returns>The silo builder.</returns>
        public static ISiloHostBuilder UseConfiguration(this ISiloHostBuilder builder, ClusterConfiguration configuration)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            return builder.ConfigureServices((context, services) =>
            {
                services.AddLegacyClusterConfigurationSupport(configuration);
            });
        }

        /// <summary>
        /// Loads <see cref="ClusterConfiguration"/> using <see cref="ClusterConfiguration.StandardLoad"/>.
        /// </summary>
        /// <param name="builder">The host builder.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloHostBuilder LoadClusterConfiguration(this ISiloHostBuilder builder)
        {
            var configuration = new ClusterConfiguration();
            configuration.StandardLoad();
            return builder.UseConfiguration(configuration);
        }

        /// <summary>
        /// Configures a localhost silo.
        /// </summary>
        /// <param name="builder">The host builder.</param>
        /// <param name="siloPort">The silo-to-silo communication port.</param>
        /// <param name="gatewayPort">The client-to-silo communication port.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloHostBuilder ConfigureLocalHostPrimarySilo(this ISiloHostBuilder builder, int siloPort = 22222, int gatewayPort = 40000)
        {
            builder.ConfigureSiloName(Silo.PrimarySiloName);
            return builder.UseConfiguration(ClusterConfiguration.LocalhostPrimarySilo(siloPort, gatewayPort));
        }

        public static IServiceCollection AddLegacyClusterConfigurationSupport(this IServiceCollection services, ClusterConfiguration configuration)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));

            if (services.TryGetClusterConfiguration() != null)
            {
                throw new InvalidOperationException("Cannot configure legacy ClusterConfiguration support twice");
            }

            // these will eventually be removed once our code doesn't depend on the old ClientConfiguration
            services.AddSingleton(configuration);
            services.TryAddSingleton<LegacyConfigurationWrapper>();
            services.TryAddSingleton(sp => sp.GetRequiredService<LegacyConfigurationWrapper>().ClusterConfig.Globals);
            services.TryAddTransient(sp => sp.GetRequiredService<LegacyConfigurationWrapper>().NodeConfig);
            services.TryAddSingleton<Factory<NodeConfiguration>>(
                sp =>
                {
                    var initializationParams = sp.GetRequiredService<LegacyConfigurationWrapper>();
                    return () => initializationParams.NodeConfig;
                });

            services.Configure<SiloOptions>(options =>
            {
                if (string.IsNullOrWhiteSpace(options.ClusterId) && !string.IsNullOrWhiteSpace(configuration.Globals.ClusterId))
                {
                    options.ClusterId = configuration.Globals.ClusterId;
                }

                if (options.ServiceId == Guid.Empty)
                {
                    options.ServiceId = configuration.Globals.ServiceId;
                }
                options.FastKillOnCancelKeyPress = configuration.Globals.FastKillOnCancelKeyPress;
            });

            services.Configure<MultiClusterOptions>(options =>
            {
                var globals = configuration.Globals;
                if (globals.HasMultiClusterNetwork)
                {
                    options.HasMultiClusterNetwork = true;
                    options.BackgroundGossipInterval = globals.BackgroundGossipInterval;
                    options.DefaultMultiCluster = globals.DefaultMultiCluster?.ToList();
                    options.GlobalSingleInstanceNumberRetries = globals.GlobalSingleInstanceNumberRetries;
                    options.GlobalSingleInstanceRetryInterval = globals.GlobalSingleInstanceRetryInterval;
                    options.MaxMultiClusterGateways = globals.MaxMultiClusterGateways;
                    options.UseGlobalSingleInstanceByDefault = globals.UseGlobalSingleInstanceByDefault;
                    foreach(GlobalConfiguration.GossipChannelConfiguration channelConfig in globals.GossipChannels)
                    {
                        options.GossipChannels.Add(GlobalConfiguration.Remap(channelConfig.ChannelType), channelConfig.ConnectionString);
                    }
                }
            });

            services.TryAddFromExisting<IMessagingConfiguration, GlobalConfiguration>();

            services.AddOptions<SiloStatisticsOptions>()
                .Configure<NodeConfiguration>((options, nodeConfig) => LegacyConfigurationExtensions.CopyStatisticsOptions(nodeConfig, options))
                .Configure<NodeConfiguration>((options, nodeConfig) =>
                {
                    options.LoadSheddingEnabled = nodeConfig.LoadSheddingEnabled;
                    options.LoadSheddingLimit = nodeConfig.LoadSheddingLimit;
                })
                .Configure<GlobalConfiguration>((options, config) =>
                {
                    options.DeploymentLoadPublisherRefreshTime = config.DeploymentLoadPublisherRefreshTime;
                });

            // Translate legacy configuration to new Options
            services.AddOptions<SiloMessagingOptions>()
                .Configure<GlobalConfiguration>((options, config) =>
                {
                    LegacyConfigurationExtensions.CopyCommonMessagingOptions(config, options);
                    options.SiloSenderQueues = config.SiloSenderQueues;
                    options.GatewaySenderQueues = config.GatewaySenderQueues;
                    options.MaxForwardCount = config.MaxForwardCount;
                    options.ClientDropTimeout = config.ClientDropTimeout;
                    options.ClientRegistrationRefresh = config.ClientRegistrationRefresh;
                    options.MaxRequestProcessingTime = config.MaxRequestProcessingTime;
                    options.AssumeHomogenousSilosForTesting = config.AssumeHomogenousSilosForTesting;
                })
                .Configure<NodeConfiguration>((options, config) =>
                {
                    options.PropagateActivityId = config.PropagateActivityId;
                    LimitValue requestLimit = config.LimitManager.GetLimit(LimitNames.LIMIT_MAX_ENQUEUED_REQUESTS);
                    options.MaxEnqueuedRequestsSoftLimit = requestLimit.SoftLimitThreshold;
                    options.MaxEnqueuedRequestsHardLimit = requestLimit.HardLimitThreshold;
                    LimitValue statelessWorkerRequestLimit = config.LimitManager.GetLimit(LimitNames.LIMIT_MAX_ENQUEUED_REQUESTS_STATELESS_WORKER);
                    options.MaxEnqueuedRequestsSoftLimit_StatelessWorker = statelessWorkerRequestLimit.SoftLimitThreshold;
                    options.MaxEnqueuedRequestsHardLimit_StatelessWorker = statelessWorkerRequestLimit.HardLimitThreshold;
                });

            services.Configure<NetworkingOptions>(options => LegacyConfigurationExtensions.CopyNetworkingOptions(configuration.Globals, options));

            services.AddOptions<EndpointOptions>()
                .Configure<IOptions<SiloOptions>>((options, siloOptions) =>
                {
                    var nodeConfig = configuration.GetOrCreateNodeConfigurationForSilo(siloOptions.Value.SiloName);
                    if (options.IPAddress == null && string.IsNullOrWhiteSpace(options.HostNameOrIPAddress))
                    {
                        options.IPAddress = nodeConfig.Endpoint.Address;
                        options.Port = nodeConfig.Endpoint.Port;
                    }
                    if (options.ProxyPort == 0 && nodeConfig.ProxyGatewayEndpoint != null)
                    {
                        options.ProxyPort = nodeConfig.ProxyGatewayEndpoint.Port;
                    }
                });

            services.Configure<SerializationProviderOptions>(options =>
            {
                options.SerializationProviders = configuration.Globals.SerializationProviders;
                options.FallbackSerializationProvider = configuration.Globals.FallbackSerializationProvider;
            });

            services.Configure<TelemetryOptions>(options =>
            {
                LegacyConfigurationExtensions.CopyTelemetryOptions(configuration.Defaults.TelemetryConfiguration, services, options);
            });

            services.AddOptions<GrainClassOptions>().Configure<IOptions<SiloOptions>>((options, siloOptions) =>
            {
                var nodeConfig = configuration.GetOrCreateNodeConfigurationForSilo(siloOptions.Value.SiloName);
                options.ExcludedGrainTypes.AddRange(nodeConfig.ExcludedGrainTypes);
            });

            LegacyMembershipConfigurator.ConfigureServices(configuration.Globals, services);

            services.AddOptions<SchedulingOptions>()
                .Configure<GlobalConfiguration>((options, config) =>
                {
                    options.AllowCallChainReentrancy = config.AllowCallChainReentrancy;
                    options.PerformDeadlockDetection = config.PerformDeadlockDetection;
                })
                .Configure<NodeConfiguration>((options, nodeConfig) =>
                {
                    options.MaxActiveThreads = nodeConfig.MaxActiveThreads;
                    options.DelayWarningThreshold = nodeConfig.DelayWarningThreshold;
                    options.ActivationSchedulingQuantum = nodeConfig.ActivationSchedulingQuantum;
                    options.TurnWarningLengthThreshold = nodeConfig.TurnWarningLengthThreshold;
                    options.EnableWorkerThreadInjection = nodeConfig.EnableWorkerThreadInjection;
                    LimitValue itemLimit = nodeConfig.LimitManager.GetLimit(LimitNames.LIMIT_MAX_PENDING_ITEMS);
                    options.MaxPendingWorkItemsSoftLimit = itemLimit.SoftLimitThreshold;
                    options.MaxPendingWorkItemsHardLimit = itemLimit.HardLimitThreshold;
                });

            services.AddOptions<GrainCollectionOptions>().Configure<GlobalConfiguration>((options, config) =>
            {
                options.CollectionQuantum = config.CollectionQuantum;
                options.CollectionAge = config.Application.DefaultCollectionAgeLimit;
                foreach (GrainTypeConfiguration grainConfig in config.Application.ClassSpecific)
                {
                    if(grainConfig.CollectionAgeLimit.HasValue)
                    {
                        options.ClassSpecificCollectionAge.Add(grainConfig.FullTypeName, grainConfig.CollectionAgeLimit.Value);
                    }
                };
            });

            services.TryAddSingleton<LegacyProviderConfigurator.ScheduleTask>(sp =>
            {
                OrleansTaskScheduler scheduler = sp.GetRequiredService<OrleansTaskScheduler>();
                SystemTarget fallbackSystemTarget = sp.GetRequiredService<FallbackSystemTarget>();
                return (taskFunc) => scheduler.QueueTask(taskFunc, fallbackSystemTarget.SchedulingContext);
            });
            LegacyProviderConfigurator<ISiloLifecycle>.ConfigureServices(configuration.Globals.ProviderConfigurations, services, SiloDefaultProviderInitStage, SiloDefaultProviderStartStage);

            services.AddOptions<GrainPlacementOptions>().Configure<GlobalConfiguration>((options, config) =>
            {
                options.DefaultPlacementStrategy = config.DefaultPlacementStrategy;
                options.ActivationCountPlacementChooseOutOf = config.ActivationCountBasedPlacementChooseOutOf;
            });

            services.AddOptions<StaticClusterDeploymentOptions>().Configure<ClusterConfiguration>((options, config) =>
            {
                options.SiloNames = config.Overrides.Keys.ToList();
            });

            // add grain service configs as keyed services
            short id = 0;
            foreach (IGrainServiceConfiguration grainServiceConfiguration in configuration.Globals.GrainServiceConfigurations.GrainServices.Values)
            {
                services.AddSingletonKeyedService<long, IGrainServiceConfiguration>(id++, (sp, k) => grainServiceConfiguration);
            }
            // populate grain service options
            id = 0;
            services.AddOptions<GrainServiceOptions>().Configure<GlobalConfiguration>((options, config) =>
            {
                foreach(IGrainServiceConfiguration grainServiceConfiguration in config.GrainServiceConfigurations.GrainServices.Values)
                {
                    options.GrainServices.Add(new KeyValuePair<string, short>(grainServiceConfiguration.ServiceType, id++));
                }
            });

            services.AddOptions<ConsistentRingOptions>().Configure<GlobalConfiguration>((options, config) =>
            {
                options.UseVirtualBucketsConsistentRing = config.UseVirtualBucketsConsistentRing;
                options.NumVirtualBucketsConsistentRing = config.NumVirtualBucketsConsistentRing;
            });

            services.AddOptions<MembershipOptions>()
                .Configure<GlobalConfiguration>((options, config) =>
                {
                    options.NumMissedTableIAmAliveLimit = config.NumMissedTableIAmAliveLimit;
                    options.LivenessEnabled = config.LivenessEnabled;
                    options.ProbeTimeout = config.ProbeTimeout;
                    options.TableRefreshTimeout = config.TableRefreshTimeout;
                    options.DeathVoteExpirationTimeout = config.DeathVoteExpirationTimeout;
                    options.IAmAliveTablePublishTimeout = config.IAmAliveTablePublishTimeout;
                    options.MaxJoinAttemptTime = config.MaxJoinAttemptTime;
                    options.ExpectedClusterSize = config.ExpectedClusterSize;
                    options.ValidateInitialConnectivity = config.ValidateInitialConnectivity;
                    options.NumMissedProbesLimit = config.NumMissedProbesLimit;
                    options.UseLivenessGossip = config.UseLivenessGossip;
                    options.NumProbedSilos = config.NumProbedSilos;
                    options.NumVotesForDeathDeclaration = config.NumVotesForDeathDeclaration;
                })
                .Configure<ClusterConfiguration>((options, config) =>
                {
                    options.IsRunningAsUnitTest = config.IsRunningAsUnitTest;
                });

            services.AddOptions<ReminderOptions>()
                .Configure<GlobalConfiguration>((options, config) =>
                {
                    options.ReminderService = GlobalConfiguration.Remap(config.ReminderServiceType);
                    options.ReminderTableAssembly = config.ReminderTableAssembly;
                    options.UseMockReminderTable = config.UseMockReminderTable;
                    options.MockReminderTableTimeout = config.MockReminderTableTimeout;
                });

            services.AddOptions<VersioningOptions>()
                .Configure<GlobalConfiguration>((options, config) =>
                {
                    options.DefaultCompatibilityStrategy = config.DefaultCompatibilityStrategy?.GetType().Name ?? VersioningOptions.DEFAULT_COMPATABILITY_STRATEGY;
                    options.DefaultVersionSelectorStrategy = config.DefaultVersionSelectorStrategy?.GetType().Name ?? VersioningOptions.DEFAULT_VERSION_SELECTOR_STRATEGY;
                });

            services.AddOptions<ThreadPoolOptions>()
                .Configure<NodeConfiguration>((options, config) =>
                {
                    options.MinDotNetThreadPoolSize = config.MinDotNetThreadPoolSize;
                });

            services.AddOptions<ServicePointOptions>()
                .Configure<NodeConfiguration>((options, config) =>
                {
                    options.DefaultConnectionLimit = config.DefaultConnectionLimit;
                    options.Expect100Continue = config.Expect100Continue;
                    options.UseNagleAlgorithm = config.UseNagleAlgorithm;
                });

            services.AddOptions<StorageOptions>()
                .Configure<GlobalConfiguration>((options, config) =>
                {
                    options.DataConnectionString = config.DataConnectionString;
                    options.DataConnectionStringForReminders = config.DataConnectionStringForReminders;
                });

            services.AddOptions<AdoNetOptions>()
                .Configure<GlobalConfiguration>((options, config) =>
                {
                    options.Invariant = config.AdoInvariant;
                    options.InvariantForReminders = config.AdoInvariantForReminders;
                });

            services.AddOptions<TypeManagementOptions>()
                .Configure<GlobalConfiguration>((options, config) =>
                {
                    options.TypeMapRefreshInterval = config.TypeMapRefreshInterval;
                });

            services.AddOptions<GrainDirectoryOptions>()
                .Configure<GlobalConfiguration>((options, config) =>
                {
                    options.CachingStrategy = GlobalConfiguration.Remap(config.DirectoryCachingStrategy);
                    options.CacheSize = config.CacheSize;
                    options.InitialCacheTTL = config.InitialCacheTTL;
                    options.MaximumCacheTTL = config.MaximumCacheTTL;
                    options.CacheTTLExtensionFactor = config.CacheTTLExtensionFactor;
                    options.LazyDeregistrationDelay = config.DirectoryLazyDeregistrationDelay;
                });

            return services;
        }

        public static ClusterConfiguration TryGetClusterConfiguration(this IServiceCollection services)
        {
            return services
                .FirstOrDefault(s => s.ServiceType == typeof(ClusterConfiguration))
                ?.ImplementationInstance as ClusterConfiguration;
        }
    }
}
