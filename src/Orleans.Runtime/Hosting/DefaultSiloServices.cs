using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Configuration;
using Orleans.Configuration.Validators;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.ConsistentRing;
using Orleans.Runtime.Counters;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.MembershipService;
using Orleans.Metadata;
using Orleans.Runtime.Messaging;
using Orleans.Runtime.Placement;
using Orleans.Runtime.Providers;
using Orleans.Runtime.ReminderService;
using Orleans.Runtime.Scheduler;
using Orleans.Runtime.Versions;
using Orleans.Runtime.Versions.Compatibility;
using Orleans.Runtime.Versions.Selector;
using Orleans.Serialization;
using Orleans.Statistics;
using Orleans.Timers;
using Orleans.Versions;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;
using Orleans.Providers;
using Orleans.Runtime;
using Microsoft.Extensions.Logging;
using Orleans.Runtime.Utilities;
using System;
using System.Reflection;
using System.Linq;
using Microsoft.Extensions.Options;
using Orleans.Timers.Internal;
using Microsoft.AspNetCore.Connections;
using Orleans.Networking.Shared;
using Orleans.Configuration.Internal;
using Orleans.Runtime.Metadata;
using Orleans.GrainReferences;
using Orleans.Serialization.TypeSystem;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Cloning;

namespace Orleans.Hosting
{
    internal static class DefaultSiloServices
    {
        internal static void AddDefaultServices(IServiceCollection services)
        {
            services.AddOptions();

            services.AddTransient<IConfigurationValidator, EndpointOptionsValidator>();

            // Options logging
            services.TryAddSingleton(typeof(IOptionFormatter<>), typeof(DefaultOptionsFormatter<>));
            services.TryAddSingleton(typeof(IOptionFormatterResolver<>), typeof(DefaultOptionsFormatterResolver<>));

            // Register system services.
            services.TryAddSingleton<ILocalSiloDetails, LocalSiloDetails>();
            services.TryAddSingleton<ISiloHost, SiloHost>();
            services.TryAddTransient<ILifecycleSubject, LifecycleSubject>();
            services.TryAddSingleton<SiloLifecycleSubject>();
            services.TryAddFromExisting<ISiloLifecycleSubject, SiloLifecycleSubject>();
            services.TryAddFromExisting<ISiloLifecycle, SiloLifecycleSubject>();
            services.AddSingleton<SiloOptionsLogger>();
            services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, SiloOptionsLogger>();
            services.PostConfigure<SiloMessagingOptions>(options =>
            {
                //
                // Assign environment specific defaults post configuration if user did not configured otherwise.
                //

                if (options.SiloSenderQueues == 0)
                {
                    options.SiloSenderQueues = Environment.ProcessorCount;
                }

                if (options.GatewaySenderQueues == 0)
                {
                    options.GatewaySenderQueues = Environment.ProcessorCount;
                }
            });
            services.TryAddSingleton<TelemetryManager>();
            services.TryAddFromExisting<ITelemetryProducer, TelemetryManager>();

            services.TryAddSingleton<IAppEnvironmentStatistics, AppEnvironmentStatistics>();
            services.TryAddSingleton<IHostEnvironmentStatistics, NoOpHostEnvironmentStatistics>();
            services.TryAddSingleton<SiloStatisticsManager>();
            services.TryAddSingleton<ApplicationRequestsStatisticsGroup>();
            services.TryAddSingleton<StageAnalysisStatisticsGroup>();
            services.TryAddSingleton<SchedulerStatisticsGroup>();
            services.TryAddSingleton<OverloadDetector>();

            services.TryAddSingleton<FallbackSystemTarget>();
            services.TryAddSingleton<LifecycleSchedulingSystemTarget>();

            services.AddLogging();
            services.TryAddSingleton<ITimerRegistry, TimerRegistry>();
            services.TryAddSingleton<IReminderRegistry, ReminderRegistry>();
            services.AddTransient<IConfigurationValidator, ReminderOptionsValidator>();
            services.TryAddSingleton<GrainRuntime>();
            services.TryAddSingleton<IGrainRuntime, GrainRuntime>();
            services.TryAddSingleton<IGrainCancellationTokenRuntime, GrainCancellationTokenRuntime>();
            services.AddTransient<CancellationSourcesExtension>();
            services.AddTransientKeyedService<Type, IGrainExtension>(typeof(ICancellationSourcesExtension), (sp, _) => sp.GetRequiredService<CancellationSourcesExtension>());
            services.TryAddSingleton<OrleansTaskScheduler>();
            services.TryAddSingleton<GrainFactory>(sp => sp.GetService<InsideRuntimeClient>().ConcreteGrainFactory);
            services.TryAddSingleton<GrainInterfaceTypeToGrainTypeResolver>();
            services.TryAddFromExisting<IGrainFactory, GrainFactory>();
            services.TryAddFromExisting<IInternalGrainFactory, GrainFactory>();
            services.TryAddSingleton<IGrainReferenceRuntime, GrainReferenceRuntime>();
            services.TryAddSingleton<GrainReferenceActivator>();
            services.AddSingleton<IGrainReferenceActivatorProvider, NewGrainReferenceActivatorProvider>();
            services.AddSingleton<IGrainReferenceActivatorProvider, UntypedGrainReferenceActivatorProvider>();
            services.AddSingleton<IConfigureGrainContextProvider, MayInterleaveConfiguratorProvider>();
            services.AddSingleton<IConfigureGrainTypeComponents, ReentrantSharedComponentsConfigurator>();
            services.TryAddSingleton<NewRpcProvider>();
            services.TryAddSingleton<GrainReferenceKeyStringConverter>();
            services.AddSingleton<GrainVersionManifest>();
            services.TryAddSingleton<GrainBindingsResolver>();
            services.TryAddSingleton<GrainTypeComponentsResolver>();
            services.TryAddSingleton<ActivationDirectory>();
            services.AddSingleton<ActivationCollector>();
            services.AddFromExisting<IActivationCollector, ActivationCollector>();
            services.AddFromExisting<IActivationWorkingSetObserver, ActivationCollector>();

            services.AddSingleton<ActivationWorkingSet>();
            services.AddFromExisting<IActivationWorkingSet, ActivationWorkingSet>();
            services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, ActivationWorkingSet>();

            // Directory
            services.TryAddSingleton<LocalGrainDirectory>();
            services.TryAddFromExisting<ILocalGrainDirectory, LocalGrainDirectory>();
            services.AddSingleton<GrainLocator>();
            services.AddSingleton<GrainLocatorResolver>();
            services.AddSingleton<DhtGrainLocator>(sp => DhtGrainLocator.FromLocalGrainDirectory(sp.GetService<LocalGrainDirectory>()));
            services.AddSingleton<GrainDirectoryResolver>();
            services.AddSingleton<IGrainDirectoryResolver, GenericGrainDirectoryResolver>();
            services.AddSingleton<CachedGrainLocator>();
            services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, CachedGrainLocator>();
            services.AddSingleton<ClientGrainLocator>();

            services.TryAddSingleton<MessageCenter>();
            services.TryAddFromExisting<IMessageCenter, MessageCenter>();
            services.TryAddSingleton(FactoryUtility.Create<MessageCenter, Gateway>);
            services.TryAddSingleton<IConnectedClientCollection>(sp => (IConnectedClientCollection)sp.GetRequiredService<MessageCenter>().Gateway ?? new EmptyConnectedClientCollection());
            services.TryAddSingleton<Dispatcher>(sp => sp.GetRequiredService<Catalog>().Dispatcher);
            services.TryAddSingleton<ActivationMessageScheduler>(sp => sp.GetRequiredService<Catalog>().ActivationMessageScheduler);
            services.TryAddSingleton<InsideRuntimeClient>();
            services.TryAddFromExisting<IRuntimeClient, InsideRuntimeClient>();
            services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, InsideRuntimeClient>();
            services.TryAddSingleton<IGrainServiceFactory, GrainServiceFactory>();

            services.TryAddSingleton<IFatalErrorHandler, FatalErrorHandler>();

            services.TryAddSingleton<DeploymentLoadPublisher>();

            services.TryAddSingleton<IAsyncTimerFactory, AsyncTimerFactory>();
            services.TryAddSingleton<MembershipTableManager>();
            services.AddFromExisting<IHealthCheckParticipant, MembershipTableManager>();
            services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, MembershipTableManager>();
            services.TryAddSingleton<MembershipSystemTarget>();
            services.AddFromExisting<IMembershipService, MembershipSystemTarget>();
            services.TryAddSingleton<IMembershipGossiper, MembershipGossiper>();
            services.TryAddSingleton<IRemoteSiloProber, RemoteSiloProber>();
            services.TryAddSingleton<SiloStatusOracle>();
            services.TryAddFromExisting<ISiloStatusOracle, SiloStatusOracle>();
            services.AddSingleton<ClusterHealthMonitor>();
            services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, ClusterHealthMonitor>();
            services.AddFromExisting<IHealthCheckParticipant, ClusterHealthMonitor>();
            services.AddSingleton<ProbeRequestMonitor>();
            services.AddSingleton<LocalSiloHealthMonitor>();
            services.AddFromExisting<ILocalSiloHealthMonitor, LocalSiloHealthMonitor>();
            services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, LocalSiloHealthMonitor>();
            services.AddSingleton<MembershipAgent>();
            services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, MembershipAgent>();
            services.AddFromExisting<IHealthCheckParticipant, MembershipAgent>();
            services.AddSingleton<MembershipTableCleanupAgent>();
            services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, MembershipTableCleanupAgent>();
            services.AddFromExisting<IHealthCheckParticipant, MembershipTableCleanupAgent>();
            services.AddSingleton<SiloStatusListenerManager>();
            services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, SiloStatusListenerManager>();
            services.AddSingleton<ClusterMembershipService>();
            services.TryAddFromExisting<IClusterMembershipService, ClusterMembershipService>();
            services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, ClusterMembershipService>();

            services.TryAddSingleton<ClientDirectory>();
            services.AddFromExisting<ILocalClientDirectory, ClientDirectory>();
            services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, ClientDirectory>();
            
            services.TryAddSingleton<SiloProviderRuntime>();
            services.TryAddFromExisting<IProviderRuntime, SiloProviderRuntime>();

            services.TryAddSingleton<MessageFactory>();

            services.TryAddSingleton(FactoryUtility.Create<GrainDirectoryPartition>);

            // Placement
            services.AddSingleton<IConfigurationValidator, ActivationCountBasedPlacementOptionsValidator>();
            services.AddSingleton<PlacementService>();
            services.AddSingleton<PlacementStrategyResolver>();
            services.AddSingleton<PlacementDirectorResolver>();
            services.AddSingleton<IPlacementStrategyResolver, ClientObserverPlacementStrategyResolver>();

            // Configure the default placement strategy.
            services.TryAddSingleton<PlacementStrategy, RandomPlacement>();

            // Placement directors
            services.AddPlacementDirector<RandomPlacement, RandomPlacementDirector>();
            services.AddPlacementDirector<PreferLocalPlacement, PreferLocalPlacementDirector>();
            services.AddPlacementDirector<StatelessWorkerPlacement, StatelessWorkerDirector>();
            services.Replace(new ServiceDescriptor(typeof(StatelessWorkerPlacement), sp => new StatelessWorkerPlacement(), ServiceLifetime.Singleton));
            services.AddPlacementDirector<ActivationCountBasedPlacement, ActivationCountPlacementDirector>();
            services.AddPlacementDirector<HashBasedPlacement, HashBasedPlacementDirector>();
            services.AddPlacementDirector<ClientObserversPlacement, ClientObserversPlacementDirector>();
            services.AddPlacementDirector<SiloRoleBasedPlacement, SiloRoleBasedPlacementDirector>();

            // Versioning
            services.TryAddSingleton<VersionSelectorManager>();
            services.TryAddSingleton<CachedVersionSelectorManager>();
            // Version selector strategy
            if (!services.Any(x => x.ServiceType == typeof(IVersionStore)))
            {
                services.TryAddSingleton<GrainVersionStore>();
                services.AddFromExisting<IVersionStore, GrainVersionStore>();
            }
            services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, GrainVersionStore>();
            services.AddSingletonNamedService<VersionSelectorStrategy, AllCompatibleVersions>(nameof(AllCompatibleVersions));
            services.AddSingletonNamedService<VersionSelectorStrategy, LatestVersion>(nameof(LatestVersion));
            services.AddSingletonNamedService<VersionSelectorStrategy, MinimumVersion>(nameof(MinimumVersion));
            // Versions selectors
            services.AddSingletonKeyedService<Type, IVersionSelector, MinimumVersionSelector>(typeof(MinimumVersion));
            services.AddSingletonKeyedService<Type, IVersionSelector, LatestVersionSelector>(typeof(LatestVersion));
            services.AddSingletonKeyedService<Type, IVersionSelector, AllCompatibleVersionsSelector>(typeof(AllCompatibleVersions));

            // Compatibility
            services.TryAddSingleton<CompatibilityDirectorManager>();
            // Compatability strategy
            services.AddSingletonNamedService<CompatibilityStrategy, AllVersionsCompatible>(nameof(AllVersionsCompatible));
            services.AddSingletonNamedService<CompatibilityStrategy, BackwardCompatible>(nameof(BackwardCompatible));
            services.AddSingletonNamedService<CompatibilityStrategy, StrictVersionCompatible>(nameof(StrictVersionCompatible));
            // Compatability directors
            services.AddSingletonKeyedService<Type, ICompatibilityDirector, BackwardCompatilityDirector>(typeof(BackwardCompatible));
            services.AddSingletonKeyedService<Type, ICompatibilityDirector, AllVersionsCompatibilityDirector>(typeof(AllVersionsCompatible));
            services.AddSingletonKeyedService<Type, ICompatibilityDirector, StrictVersionCompatibilityDirector>(typeof(StrictVersionCompatible));

            services.TryAddSingleton<Factory<IGrainRuntime>>(sp => () => sp.GetRequiredService<IGrainRuntime>());

            // Grain activation
            services.TryAddSingleton<PlacementService>();
            services.TryAddSingleton<Catalog>();
            services.AddFromExisting<IHealthCheckParticipant, Catalog>();
            services.TryAddSingleton<GrainContextActivator>();
            services.AddSingleton<IConfigureGrainTypeComponents, ConfigureDefaultGrainActivator>();
            services.TryAddSingleton<GrainReferenceActivator>();
            services.TryAddSingleton<IGrainContextActivatorProvider, ActivationDataActivatorProvider>();
            services.TryAddSingleton<IGrainContextAccessor, GrainContextAccessor>();
            services.AddSingleton<IncomingRequestMonitor>();
            services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, IncomingRequestMonitor>();
            services.AddFromExisting<IActivationWorkingSetObserver, IncomingRequestMonitor>();

            services.TryAddSingleton<IConsistentRingProvider>(
                sp =>
                {
                    // TODO: make this not sux - jbragg
                    var consistentRingOptions = sp.GetRequiredService<IOptions<ConsistentRingOptions>>().Value;
                    var siloDetails = sp.GetRequiredService<ILocalSiloDetails>();
                    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                    if (consistentRingOptions.UseVirtualBucketsConsistentRing)
                    {
                        return new VirtualBucketsRingProvider(siloDetails.SiloAddress, loggerFactory, consistentRingOptions.NumVirtualBucketsConsistentRing);
                    }

                    return new ConsistentRingProvider(siloDetails.SiloAddress, loggerFactory);
                });

            services.TryAddSingleton(typeof(IKeyedServiceCollection<,>), typeof(KeyedServiceCollection<,>));

            services.AddSingleton<IConfigureOptions<GrainTypeOptions>, DefaultGrainTypeOptionsProvider>();

            // Type metadata
            services.AddSingleton<SiloManifestProvider>();
            services.AddSingleton<GrainClassMap>(sp => sp.GetRequiredService<SiloManifestProvider>().GrainTypeMap);
            services.AddSingleton<GrainTypeResolver>();
            services.AddSingleton<IGrainTypeProvider, AttributeGrainTypeProvider>();
            services.AddSingleton<GrainPropertiesResolver>();
            services.AddSingleton<GrainInterfaceTypeResolver>();
            services.AddSingleton<IGrainInterfaceTypeProvider, AttributeGrainInterfaceTypeProvider>();
            services.AddSingleton<IGrainInterfacePropertiesProvider, AttributeGrainInterfacePropertiesProvider>();
            services.AddSingleton<IGrainPropertiesProvider, AttributeGrainPropertiesProvider>();
            services.AddSingleton<IGrainPropertiesProvider, AttributeGrainBindingsProvider>();
            services.AddSingleton<IGrainInterfacePropertiesProvider, TypeNameGrainPropertiesProvider>();
            services.AddSingleton<IGrainPropertiesProvider, TypeNameGrainPropertiesProvider>();
            services.AddSingleton<IGrainPropertiesProvider, ImplementedInterfaceProvider>();
            services.AddSingleton<ClusterManifestProvider>();
            services.AddFromExisting<IClusterManifestProvider, ClusterManifestProvider>();
            services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, ClusterManifestProvider>();

            //Add default option formatter if none is configured, for options which are required to be configured
            services.ConfigureFormatter<SiloOptions>();
            services.ConfigureFormatter<SchedulingOptions>();
            services.ConfigureFormatter<PerformanceTuningOptions>();
            services.ConfigureFormatter<ConnectionOptions>();
            services.ConfigureFormatter<SiloMessagingOptions>();
            services.ConfigureFormatter<ClusterMembershipOptions>();
            services.ConfigureFormatter<GrainDirectoryOptions>();
            services.ConfigureFormatter<ActivationCountBasedPlacementOptions>();
            services.ConfigureFormatter<GrainCollectionOptions>();
            services.ConfigureFormatter<GrainVersioningOptions>();
            services.ConfigureFormatter<ConsistentRingOptions>();
            services.ConfigureFormatter<StatisticsOptions>();
            services.ConfigureFormatter<TelemetryOptions>();
            services.ConfigureFormatter<LoadSheddingOptions>();
            services.ConfigureFormatter<EndpointOptions>();
            services.ConfigureFormatter<ClusterOptions>();

            // This validator needs to construct the IMembershipOracle and the IMembershipTable
            // so move it in the end so other validator are called first
            services.AddTransient<IConfigurationValidator, ClusterOptionsValidator>();
            services.AddTransient<IConfigurationValidator, SiloClusteringValidator>();
            services.AddTransient<IConfigurationValidator, DevelopmentClusterMembershipOptionsValidator>();
            services.AddTransient<IConfigurationValidator, GrainTypeOptionsValidator>();

            // Enable hosted client.
            services.TryAddSingleton<HostedClient>();
            services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, HostedClient>();
            services.TryAddSingleton<InternalClusterClient>();
            services.TryAddFromExisting<IInternalClusterClient, InternalClusterClient>();
            services.TryAddFromExisting<IClusterClient, InternalClusterClient>();

            // Enable collection specific Age limits
            services.AddOptions<GrainCollectionOptions>()
                .Configure<IOptions<GrainTypeOptions>>((options, grainTypeOptions) =>
                {
                    foreach (var grainClass in grainTypeOptions.Value.Classes)
                    {
                        var attr = grainClass.GetCustomAttribute<CollectionAgeLimitAttribute>();
                        if (attr != null)
                        {
                            var className = RuntimeTypeNameFormatter.Format(grainClass);
                            options.ClassSpecificCollectionAge[className] = attr.Amount;
                        }
                    }
                });

            // Validate all CollectionAgeLimit values for the right configuration.
            services.AddTransient<IConfigurationValidator, GrainCollectionOptionsValidator>();

            services.AddTransient<IConfigurationValidator, LoadSheddingValidator>();

            services.TryAddSingleton<ITimerManager, TimerManagerImpl>();

            // persistent state facet support
            services.TryAddSingleton<IPersistentStateFactory, PersistentStateFactory>();
            services.TryAddSingleton(typeof(IAttributeToFactoryMapper<PersistentStateAttribute>), typeof(PersistentStateAttributeMapper));

            // Networking
            services.TryAddSingleton<ConnectionCommon>();
            services.TryAddSingleton<ConnectionManager>();
            services.TryAddSingleton<ConnectionPreambleHelper>();
            services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>, ConnectionManagerLifecycleAdapter<ISiloLifecycle>>();
            services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>, SiloConnectionMaintainer>();

            services.AddSingletonKeyedService<object, IConnectionFactory>(
                SiloConnectionFactory.ServicesKey,
                (sp, key) => ActivatorUtilities.CreateInstance<SocketConnectionFactory>(sp));
            services.AddSingletonKeyedService<object, IConnectionListenerFactory>(
                SiloConnectionListener.ServicesKey,
                (sp, key) => ActivatorUtilities.CreateInstance<SocketConnectionListenerFactory>(sp));
            services.AddSingletonKeyedService<object, IConnectionListenerFactory>(
                GatewayConnectionListener.ServicesKey,
                (sp, key) => ActivatorUtilities.CreateInstance<SocketConnectionListenerFactory>(sp));

            services.AddSerializer();
            services.AddSingleton<ITypeFilter, AllowOrleansTypes>();
            services.AddSingleton<ISpecializableCodec, GrainReferenceCodecProvider>();
            services.AddSingleton<ISpecializableCopier, GrainReferenceCopierProvider>();
            services.AddSingleton<OnDeserializedCallbacks>();

            services.TryAddTransient<IMessageSerializer>(sp => ActivatorUtilities.CreateInstance<MessageSerializer>(
                sp,
                sp.GetRequiredService<IOptions<ClientMessagingOptions>>().Value.MaxMessageHeaderSize,
                sp.GetRequiredService<IOptions<ClientMessagingOptions>>().Value.MaxMessageBodySize));
            services.TryAddSingleton<ConnectionFactory, SiloConnectionFactory>();
            services.AddSingleton<NetworkingTrace>();
            services.AddSingleton<RuntimeMessagingTrace>();
            services.AddFromExisting<MessagingTrace, RuntimeMessagingTrace>();

            // Use Orleans server.
            services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>, SiloConnectionListener>();
            services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>, GatewayConnectionListener>();
            services.AddSingleton<SocketSchedulers>();
            services.AddSingleton<SharedMemoryPool>();
        }

        private class AllowOrleansTypes : ITypeFilter
        {
            public bool? IsTypeNameAllowed(string typeName, string assemblyName)
            {
                if (assemblyName is { Length: > 0} && assemblyName.Contains("Orleans"))
                {
                    return true;
                }

                return null;
            }
        }
    }
}
