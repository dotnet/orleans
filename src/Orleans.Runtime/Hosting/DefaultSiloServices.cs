using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Configuration;
using Orleans.GrainDirectory;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.ConsistentRing;
using Orleans.Runtime.Counters;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.LogConsistency;
using Orleans.Runtime.MembershipService;
using Orleans.Runtime.Messaging;
using Orleans.Runtime.MultiClusterNetwork;
using Orleans.Runtime.Placement;
using Orleans.Runtime.Providers;
using Orleans.Runtime.ReminderService;
using Orleans.Runtime.Scheduler;
using Orleans.Runtime.Versions;
using Orleans.Runtime.Versions.Compatibility;
using Orleans.Runtime.Versions.Selector;
using Orleans.Serialization;
using Orleans.Streams;
using Orleans.Streams.Core;
using Orleans.Timers;
using Orleans.Versions;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Runtime.Storage;
using Orleans.Transactions;
using Orleans.LogConsistency;
using Orleans.Storage;
using Microsoft.Extensions.Logging;
using Orleans.ApplicationParts;
using Orleans.Runtime.Utilities;
using System;
using Orleans.Metadata;

namespace Orleans.Hosting
{
    internal static class DefaultSiloServices
    {
        internal static void AddDefaultServices(HostBuilderContext context, IServiceCollection services)
        {
            services.AddOptions();

            // Register system services.
            services.TryAddSingleton<ILocalSiloDetails, LocalSiloDetails>();
            services.TryAddSingleton<ISiloHost, SiloWrapper>();
            services.TryAddSingleton<SiloLifecycle>();

            services.PostConfigure<SiloMessagingOptions>(options =>
            {
                //
                // Assign environment specific defaults post configuration if user did not configured otherwise.
                //

                if (options.SiloSenderQueues==0)
                {
                    options.SiloSenderQueues = Environment.ProcessorCount;
                }

                if (options.GatewaySenderQueues==0)
                {
                    options.GatewaySenderQueues = Environment.ProcessorCount;
                }
            });
            services.TryAddSingleton<TelemetryManager>();
            services.TryAddFromExisting<ITelemetryProducer, TelemetryManager>();

            services.TryAddSingleton<ExecutorService>();
            // queue balancer contructing related
            services.TryAddTransient<StaticClusterConfigDeploymentBalancer>();
            services.TryAddTransient<DynamicClusterConfigDeploymentBalancer>();
            services.TryAddTransient<ClusterConfigDeploymentLeaseBasedBalancer>();
            services.TryAddTransient<ConsistentRingQueueBalancer>();
            services.TryAddTransient(typeof(IStreamSubscriptionObserver<>), typeof(StreamSubscriptionObserverProxy<>));

            services.TryAddSingleton<ProviderManagerSystemTarget>();
            services.TryAddSingleton<SiloUnobservedExceptionsHandler>();
            services.TryAddSingleton<StatisticsProviderManager>();
            services.AddFromExisting<IProviderManager, StatisticsProviderManager>();

            // storage providers
            services.TryAddSingleton<StorageProviderManager>();
            services.AddFromExisting<IProviderManager, StorageProviderManager>();
            services.TryAddFromExisting<IKeyedServiceCollection<string, IStorageProvider>, StorageProviderManager>(); // as named services
            services.TryAddSingleton<IStorageProvider>(sp => sp.GetRequiredService<StorageProviderManager>().GetDefaultProvider()); // default

            // log concistency providers
            services.TryAddSingleton<LogConsistencyProviderManager>();
            services.AddFromExisting<IProviderManager, LogConsistencyProviderManager>();
            services.TryAddFromExisting<IKeyedServiceCollection<string, ILogConsistencyProvider>, LogConsistencyProviderManager>(); // as named services
            services.TryAddSingleton<ILogConsistencyProvider>(sp => sp.GetRequiredService<LogConsistencyProviderManager>().GetDefaultProvider()); // default

            services.TryAddSingleton<BootstrapProviderManager>();
            services.AddFromExisting<IProviderManager, BootstrapProviderManager>();
            services.TryAddSingleton<LoadedProviderTypeLoaders>();
            services.AddLogging();
            //temporary change until runtime moved away from Logger
            services.TryAddSingleton(typeof(LoggerWrapper<>));
            services.TryAddSingleton<ITimerRegistry, TimerRegistry>();
            services.TryAddSingleton<IReminderRegistry, ReminderRegistry>();
            services.TryAddSingleton<StreamProviderManager>();
            services.AddFromExisting<IStreamProviderManager, StreamProviderManager>();
            services.TryAddFromExisting<IKeyedServiceCollection<string, IStreamProvider>, StreamProviderManager>(); // as named services
            services.AddFromExisting<IProviderManager, IStreamProviderManager>();
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
            services.TryAddSingleton<LocalGrainDirectory>();
            services.TryAddFromExisting<ILocalGrainDirectory, LocalGrainDirectory>();
            services.TryAddSingleton(sp => sp.GetRequiredService<LocalGrainDirectory>().GsiActivationMaintainer);
            services.TryAddSingleton<SiloStatisticsManager>();
            services.TryAddSingleton<ISiloPerformanceMetrics>(sp => sp.GetRequiredService<SiloStatisticsManager>().MetricsTable);
            services.TryAddFromExisting<ICorePerformanceMetrics, ISiloPerformanceMetrics>();
            services.TryAddSingleton<GrainTypeManager>();
            services.TryAddSingleton<MessageCenter>();
            services.TryAddFromExisting<IMessageCenter, MessageCenter>();
            services.TryAddFromExisting<ISiloMessageCenter, MessageCenter>();
            services.TryAddSingleton(FactoryUtility.Create<MessageCenter, Gateway>);
            services.TryAddSingleton<Dispatcher>(sp => sp.GetRequiredService<Catalog>().Dispatcher);
            services.TryAddSingleton<InsideRuntimeClient>();
            services.TryAddFromExisting<IRuntimeClient, InsideRuntimeClient>();
            services.TryAddFromExisting<ISiloRuntimeClient, InsideRuntimeClient>();
            services.TryAddFromExisting<ILifecycleParticipant<ISiloLifecycle>, InsideRuntimeClient>();
            services.TryAddSingleton<MultiClusterGossipChannelFactory>();
            services.TryAddSingleton<MultiClusterOracle>();
            services.TryAddSingleton<MultiClusterRegistrationStrategyManager>();
            services.TryAddFromExisting<IMultiClusterOracle, MultiClusterOracle>();
            services.TryAddSingleton<DeploymentLoadPublisher>();
            services.TryAddSingleton<MembershipOracle>();
            services.TryAddFromExisting<IMembershipOracle, MembershipOracle>();
            services.TryAddFromExisting<ISiloStatusOracle, MembershipOracle>();
            services.TryAddSingleton<MembershipTableFactory>();
            services.TryAddSingleton<ReminderTableFactory>();
            services.TryAddSingleton<IReminderTable>(sp => sp.GetRequiredService<ReminderTableFactory>().Create());
            services.TryAddSingleton<LocalReminderServiceFactory>();
            services.TryAddSingleton<ClientObserverRegistrar>();
            services.TryAddSingleton<SiloProviderRuntime>();
            services.TryAddFromExisting<IStreamProviderRuntime, SiloProviderRuntime>();
            services.TryAddFromExisting<IProviderRuntime, SiloProviderRuntime>();
            services.TryAddSingleton<ImplicitStreamSubscriberTable>();
            services.TryAddSingleton<MessageFactory>();

            services.TryAddSingleton<IGrainRegistrar<GlobalSingleInstanceRegistration>, GlobalSingleInstanceRegistrar>();
            services.TryAddSingleton<IGrainRegistrar<ClusterLocalRegistration>, ClusterLocalRegistrar>();
            services.TryAddSingleton<RegistrarManager>();
            services.TryAddSingleton<Factory<Grain, IMultiClusterRegistrationStrategy, ILogConsistencyProtocolServices>>(FactoryUtility.Create<Grain, IMultiClusterRegistrationStrategy, ProtocolServices>);
            services.TryAddSingleton(FactoryUtility.Create<GrainDirectoryPartition>);

            // Placement
            services.TryAddSingleton<PlacementDirectorsManager>();
            services.TryAddSingleton<IPlacementDirector<RandomPlacement>, RandomPlacementDirector>();
            services.TryAddSingleton<IActivationSelector<RandomPlacement>, RandomPlacementDirector>();
            services.TryAddSingleton<IPlacementDirector<PreferLocalPlacement>, PreferLocalPlacementDirector>();
            services.TryAddSingleton<IPlacementDirector<StatelessWorkerPlacement>, StatelessWorkerDirector>();
            services.TryAddSingleton<IActivationSelector<StatelessWorkerPlacement>, StatelessWorkerDirector>();
            services.TryAddSingleton<IPlacementDirector<ActivationCountBasedPlacement>, ActivationCountPlacementDirector>();
            services.TryAddSingleton<IPlacementDirector<HashBasedPlacement>, HashBasedPlacementDirector>();
            services.TryAddSingleton<DefaultPlacementStrategy>();
            services.TryAddSingleton<ClientObserversPlacementDirector>();

            // Versions
            services.TryAddSingleton<VersionSelectorManager>();
            services.TryAddSingleton<IVersionSelector<MinimumVersion>, MinimumVersionSelector>();
            services.TryAddSingleton<IVersionSelector<LatestVersion>, LatestVersionSelector>();
            services.TryAddSingleton<IVersionSelector<AllCompatibleVersions>, AllCompatibleVersionsSelector>();
            services.TryAddSingleton<CompatibilityDirectorManager>();
            services.TryAddSingleton<ICompatibilityDirector<BackwardCompatible>, BackwardCompatilityDirector>();
            services.TryAddSingleton<ICompatibilityDirector<AllVersionsCompatible>, AllVersionsCompatibilityDirector>();
            services.TryAddSingleton<ICompatibilityDirector<StrictVersionCompatible>, StrictVersionCompatibilityDirector>();
            services.TryAddSingleton<CachedVersionSelectorManager>();
            services.TryAddSingleton<IVersionStore, GrainVersionStore>();

            services.TryAddSingleton<Factory<IGrainRuntime>>(sp => () => sp.GetRequiredService<IGrainRuntime>());

            // Grain activation
            services.TryAddSingleton<Catalog>();
            services.TryAddSingleton<GrainCreator>();
            services.TryAddSingleton<IGrainActivator, DefaultGrainActivator>();
            services.TryAddScoped<ActivationData.GrainActivationContextFactory>();
            services.TryAddScoped<IGrainActivationContext>(sp => sp.GetRequiredService<ActivationData.GrainActivationContextFactory>().Context);

            services.TryAddSingleton<IStreamSubscriptionManagerAdmin>(sp => new StreamSubscriptionManagerAdmin(sp.GetRequiredService<IStreamProviderRuntime>()));
            services.TryAddSingleton<IConsistentRingProvider>(
                sp =>
                {
                    var globalConfig = sp.GetRequiredService<GlobalConfiguration>();
                    var siloDetails = sp.GetRequiredService<ILocalSiloDetails>();
                    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                    if (globalConfig.UseVirtualBucketsConsistentRing)
                    {
                        return new VirtualBucketsRingProvider(siloDetails.SiloAddress, loggerFactory, globalConfig.NumVirtualBucketsConsistentRing);
                    }

                    return new ConsistentRingProvider(siloDetails.SiloAddress, loggerFactory);
                });

            services.TryAddSingleton(typeof(IKeyedServiceCollection<,>), typeof(KeyedServiceCollection<,>));

            // Serialization
            services.TryAddSingleton<SerializationManager>();
            services.TryAddSingleton<ITypeResolver, CachedTypeResolver>();
            services.TryAddSingleton<IFieldUtils, FieldUtils>();
            services.AddSingleton<BinaryFormatterSerializer>();
            services.AddSingleton<BinaryFormatterISerializableSerializer>();
            services.AddFromExisting<IKeyedSerializer, BinaryFormatterISerializableSerializer>();
            services.AddSingleton<ILBasedSerializer>();
            services.AddFromExisting<IKeyedSerializer, ILBasedSerializer>();
            
            // Transactions
            services.TryAddSingleton<ITransactionAgent, TransactionAgent>();
            services.TryAddSingleton<Factory<ITransactionAgent>>(sp => () => sp.GetRequiredService<ITransactionAgent>());
            services.TryAddSingleton<ITransactionManagerService, DisabledTransactionManagerService>();

            // Application Parts
            var applicationPartManager = context.GetApplicationPartManager();
            services.TryAddSingleton<ApplicationPartManager>(applicationPartManager);
            applicationPartManager.AddApplicationPart(new AssemblyPart(typeof(RuntimeVersion).Assembly) {IsFrameworkAssembly = true});
            applicationPartManager.AddApplicationPart(new AssemblyPart(typeof(Silo).Assembly) {IsFrameworkAssembly = true});
            applicationPartManager.AddFeatureProvider(new BuiltInTypesSerializationFeaturePopulator());
            applicationPartManager.AddFeatureProvider(new AssemblyAttributeFeatureProvider<GrainInterfaceFeature>());
            applicationPartManager.AddFeatureProvider(new AssemblyAttributeFeatureProvider<GrainClassFeature>());
            applicationPartManager.AddFeatureProvider(new AssemblyAttributeFeatureProvider<SerializerFeature>());
            services.AddTransient<IConfigurationValidator, ApplicationPartValidator>();
        }
    }
}