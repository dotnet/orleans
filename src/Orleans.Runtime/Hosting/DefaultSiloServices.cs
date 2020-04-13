using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Configuration;
using Orleans.Configuration.Validators;
using Orleans.GrainDirectory;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.ConsistentRing;
using Orleans.Runtime.Counters;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.LogConsistency;
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
using Orleans.Streams;
using Orleans.Streams.Core;
using Orleans.Timers;
using Orleans.Versions;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Transactions;
using Orleans.LogConsistency;
using Microsoft.Extensions.Logging;
using Orleans.ApplicationParts;
using Orleans.Runtime.Utilities;
using System;
using System.Reflection;
using System.Linq;
using Microsoft.Extensions.Options;
using Orleans.Timers.Internal;
using Microsoft.AspNetCore.Connections;
using Orleans.Networking.Shared;
using Orleans.Configuration.Internal;

namespace Orleans.Hosting
{
    internal static class DefaultSiloServices
    {
        internal static void AddDefaultServices(IApplicationPartManager applicationPartManager, IServiceCollection services)
        {
            services.AddOptions();

            services.AddTransient<IConfigurationValidator, EndpointOptionsValidator>();

            // Options logging
            services.TryAddSingleton(typeof(IOptionFormatter<>), typeof(DefaultOptionsFormatter<>));
            services.TryAddSingleton(typeof(IOptionFormatterResolver<>), typeof(DefaultOptionsFormatterResolver<>));

            // Register system services.
            services.TryAddSingleton<ILocalSiloDetails, LocalSiloDetails>();
            services.TryAddSingleton<ISiloHost, SiloWrapper>();
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
            services.TryAddFromExisting<IStatisticsManager, SiloStatisticsManager>();
            services.TryAddSingleton<ApplicationRequestsStatisticsGroup>();
            services.TryAddSingleton<StageAnalysisStatisticsGroup>();
            services.TryAddSingleton<SchedulerStatisticsGroup>();
            services.TryAddSingleton<SerializationStatisticsGroup>();
            services.TryAddSingleton<OverloadDetector>();

            // queue balancer contructing related
            services.TryAddTransient<IStreamQueueBalancer, ConsistentRingQueueBalancer>();

            services.TryAddSingleton<FallbackSystemTarget>();
            services.TryAddSingleton<LifecycleSchedulingSystemTarget>();

            services.AddLogging();
            services.TryAddSingleton<ITimerRegistry, TimerRegistry>();
            services.TryAddSingleton<IReminderRegistry, ReminderRegistry>();
            services.TryAddSingleton<GrainRuntime>();
            services.TryAddSingleton<IGrainRuntime, GrainRuntime>();
            services.TryAddSingleton<IGrainCancellationTokenRuntime, GrainCancellationTokenRuntime>();
            services.TryAddSingleton<OrleansTaskScheduler>();
            services.TryAddSingleton<GrainFactory>(sp => sp.GetService<InsideRuntimeClient>().ConcreteGrainFactory);
            services.TryAddFromExisting<IGrainFactory, GrainFactory>();
            services.TryAddFromExisting<IInternalGrainFactory, GrainFactory>();
            services.TryAddFromExisting<IGrainReferenceConverter, GrainFactory>();
            services.TryAddSingleton<IGrainReferenceRuntime, GrainReferenceRuntime>();
            services.TryAddSingleton<TypeMetadataCache>();
            services.TryAddSingleton<ActivationDirectory>();
            services.TryAddSingleton<ActivationCollector>();
            services.TryAddSingleton<LocalGrainDirectory>();
            services.TryAddFromExisting<ILocalGrainDirectory, LocalGrainDirectory>();
            services.AddSingleton<DhtGrainLocator>();
            services.AddSingleton<IGrainDirectoryResolver, GrainDirectoryResolver>();
            if (GrainDirectoryResolver.HasAnyRegisteredGrainDirectory(services))
            {
                services.AddSingleton<IGrainLocator, GrainLocatorSelector>();
                services.AddSingleton<CachedGrainLocator>();
                services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, CachedGrainLocator>();
            }
            else
            {
                services.AddFromExisting<IGrainLocator, DhtGrainLocator>();
            }
            services.TryAddSingleton<GrainTypeManager>();
            services.TryAddSingleton<MessageCenter>();
            services.TryAddFromExisting<IMessageCenter, MessageCenter>();
            services.TryAddSingleton(FactoryUtility.Create<MessageCenter, Gateway>);
            services.TryAddSingleton<Dispatcher>(sp => sp.GetRequiredService<Catalog>().Dispatcher);
            services.TryAddSingleton<InsideRuntimeClient>();
            services.TryAddFromExisting<IRuntimeClient, InsideRuntimeClient>();
            services.TryAddFromExisting<ISiloRuntimeClient, InsideRuntimeClient>();
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

            services.TryAddSingleton<ClientObserverRegistrar>();
            services.TryAddFromExisting<ILifecycleParticipant<ISiloLifecycle>, ClientObserverRegistrar>();
            services.TryAddSingleton<SiloProviderRuntime>();
            services.TryAddFromExisting<IStreamProviderRuntime, SiloProviderRuntime>();
            services.TryAddFromExisting<IProviderRuntime, SiloProviderRuntime>();
            services.TryAddSingleton<ImplicitStreamSubscriberTable>();
            services.TryAddSingleton<MessageFactory>();

            services.TryAddSingleton<Factory<Grain, ILogConsistencyProtocolServices>>(FactoryUtility.Create<Grain, ProtocolServices>);
            services.TryAddSingleton(FactoryUtility.Create<GrainDirectoryPartition>);

            // Placement
            services.AddSingleton<IConfigurationValidator, ActivationCountBasedPlacementOptionsValidator>();
            services.TryAddSingleton<PlacementDirectorsManager>();
            services.TryAddSingleton<ClientObserversPlacementDirector>();

            // Configure the default placement strategy.
            services.TryAddSingleton<PlacementStrategy, RandomPlacement>();

            // Placement directors
            services.AddPlacementDirector<RandomPlacement, RandomPlacementDirector>();
            services.AddPlacementDirector<PreferLocalPlacement, PreferLocalPlacementDirector>();
            services.AddPlacementDirector<StatelessWorkerPlacement, StatelessWorkerDirector>();
            services.Replace(new ServiceDescriptor(typeof(StatelessWorkerPlacement), sp => new StatelessWorkerPlacement(), ServiceLifetime.Singleton));
            services.AddPlacementDirector<ActivationCountBasedPlacement, ActivationCountPlacementDirector>();
            services.AddPlacementDirector<HashBasedPlacement, HashBasedPlacementDirector>();

            // Activation selectors
            services.AddSingletonKeyedService<Type, IActivationSelector, RandomPlacementDirector>(typeof(RandomPlacement));
            services.AddSingletonKeyedService<Type, IActivationSelector, StatelessWorkerDirector>(typeof(StatelessWorkerPlacement));

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
            services.TryAddSingleton<Catalog>();
            services.AddFromExisting<IHealthCheckParticipant, Catalog>();
            services.TryAddSingleton<GrainCreator>();
            services.TryAddSingleton<IGrainActivator, DefaultGrainActivator>();
            services.TryAddScoped<ActivationData.GrainActivationContextFactory>();
            services.TryAddScoped<IGrainActivationContext>(sp => sp.GetRequiredService<ActivationData.GrainActivationContextFactory>().Context);

            services.TryAddSingleton<IStreamSubscriptionManagerAdmin>(sp => new StreamSubscriptionManagerAdmin(sp.GetRequiredService<IStreamProviderRuntime>()));
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

            // Serialization
            services.TryAddSingleton<SerializationManager>(sp=>ActivatorUtilities.CreateInstance<SerializationManager>(sp,
                sp.GetRequiredService<IOptions<SiloMessagingOptions>>().Value.LargeMessageWarningThreshold));
            services.TryAddSingleton<ITypeResolver, CachedTypeResolver>();
            services.TryAddSingleton<IFieldUtils, FieldUtils>();
            services.AddSingleton<BinaryFormatterSerializer>();
            services.AddSingleton<BinaryFormatterISerializableSerializer>();
            services.AddFromExisting<IKeyedSerializer, BinaryFormatterISerializableSerializer>();
#pragma warning disable CS0618 // Type or member is obsolete
            services.AddSingleton<ILBasedSerializer>();
            services.AddFromExisting<IKeyedSerializer, ILBasedSerializer>();
#pragma warning restore CS0618 // Type or member is obsolete

            // Transactions
            services.TryAddSingleton<ITransactionAgent, DisabledTransactionAgent>();

            // Application Parts
            services.TryAddSingleton<IApplicationPartManager>(applicationPartManager);
            applicationPartManager.AddApplicationPart(new AssemblyPart(typeof(RuntimeVersion).Assembly) { IsFrameworkAssembly = true });
            applicationPartManager.AddApplicationPart(new AssemblyPart(typeof(Silo).Assembly) { IsFrameworkAssembly = true });
            applicationPartManager.AddFeatureProvider(new BuiltInTypesSerializationFeaturePopulator());
            applicationPartManager.AddFeatureProvider(new AssemblyAttributeFeatureProvider<GrainInterfaceFeature>());
            applicationPartManager.AddFeatureProvider(new AssemblyAttributeFeatureProvider<GrainClassFeature>());
            applicationPartManager.AddFeatureProvider(new AssemblyAttributeFeatureProvider<SerializerFeature>());
            services.AddTransient<IConfigurationValidator, ApplicationPartValidator>();

            //Add default option formatter if none is configured, for options which are required to be configured
            services.ConfigureFormatter<SiloOptions>();
            services.ConfigureFormatter<SchedulingOptions>();
            services.ConfigureFormatter<PerformanceTuningOptions>();
            services.ConfigureFormatter<SerializationProviderOptions>();
            services.ConfigureFormatter<ConnectionOptions>();
            services.ConfigureFormatter<SiloMessagingOptions>();
            services.ConfigureFormatter<TypeManagementOptions>();
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

            // Enable hosted client.
            services.TryAddSingleton<HostedClient>();
            services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, HostedClient>();
            services.TryAddSingleton<InvokableObjectManager>();
            services.TryAddSingleton<InternalClusterClient>();
            services.TryAddFromExisting<IInternalClusterClient, InternalClusterClient>();
            services.TryAddFromExisting<IClusterClient, InternalClusterClient>();

            // Enable collection specific Age limits
            services.AddOptions<GrainCollectionOptions>()
                .Configure<IApplicationPartManager>((options, parts) =>
                {
                    var grainClasses = new GrainClassFeature();
                    parts.PopulateFeature(grainClasses);

                    foreach (var grainClass in grainClasses.Classes)
                    {
                        var attr = grainClass.ClassType.GetCustomAttribute<CollectionAgeLimitAttribute>();
                        if (attr != null)
                        {
                            var className = TypeUtils.GetFullName(grainClass.ClassType);
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

            services.TryAddTransient<IMessageSerializer>(sp => ActivatorUtilities.CreateInstance<MessageSerializer>(sp,
                sp.GetRequiredService<IOptions<SiloMessagingOptions>>().Value.MaxMessageHeaderSize,
                sp.GetRequiredService<IOptions<SiloMessagingOptions>>().Value.MaxMessageBodySize));
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
    }
}
