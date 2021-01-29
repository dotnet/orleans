using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Orleans.ApplicationParts;
using Orleans.Configuration;
using Orleans.Configuration.Internal;
using Orleans.Configuration.Validators;
using Orleans.GrainReferences;
using Orleans.Messaging;
using Orleans.Metadata;
using Orleans.Networking.Shared;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using Orleans.Serialization;
using Orleans.Statistics;
using Orleans.Streams;
using Orleans.Streams.Core;

namespace Orleans
{
    internal static class DefaultClientServices
    {
        public static void AddDefaultServices(IClientBuilder builder, IServiceCollection services)
        {
            // Options logging
            services.TryAddSingleton(typeof(IOptionFormatter<>), typeof(DefaultOptionsFormatter<>));
            services.TryAddSingleton(typeof(IOptionFormatterResolver<>), typeof(DefaultOptionsFormatterResolver<>));

            services.AddSingleton<ClientOptionsLogger>();
            services.AddFromExisting<ILifecycleParticipant<IClusterClientLifecycle>, ClientOptionsLogger>();
            services.TryAddSingleton<TelemetryManager>();
            services.TryAddFromExisting<ITelemetryProducer, TelemetryManager>();
            services.TryAddSingleton<IHostEnvironmentStatistics, NoOpHostEnvironmentStatistics>();
            services.TryAddSingleton<IAppEnvironmentStatistics, AppEnvironmentStatistics>();
            services.TryAddSingleton<ClientStatisticsManager>();
            services.TryAddFromExisting<IStatisticsManager, ClientStatisticsManager>();
            services.TryAddSingleton<ApplicationRequestsStatisticsGroup>();
            services.TryAddSingleton<StageAnalysisStatisticsGroup>();
            services.TryAddSingleton<SchedulerStatisticsGroup>();
            services.TryAddSingleton<SerializationStatisticsGroup>();
            services.AddLogging();
            services.TryAddSingleton<TypeMetadataCache>();
            services.TryAddSingleton<GrainBindingsResolver>();
            services.TryAddSingleton<OutsideRuntimeClient>();
            services.TryAddSingleton<ClientGrainContext>();
            services.AddFromExisting<IGrainContextAccessor, ClientGrainContext>();
            services.TryAddFromExisting<IRuntimeClient, OutsideRuntimeClient>();
            services.TryAddFromExisting<IClusterConnectionStatusListener, OutsideRuntimeClient>();
            services.TryAddSingleton<GrainFactory>();
            services.TryAddSingleton<GrainInterfaceTypeToGrainTypeResolver>();
            services.TryAddSingleton<GrainReferenceActivator>();
            services.AddSingleton<IGrainReferenceActivatorProvider, ImrGrainReferenceActivatorProvider>();
            services.AddSingleton<IGrainReferenceActivatorProvider, UntypedGrainReferenceActivatorProvider>();
            services.TryAddSingleton<ImrRpcProvider>();
            services.TryAddSingleton<ImrGrainMethodInvokerProvider>();
            services.TryAddSingleton<GrainReferenceSerializer>();
            services.TryAddSingleton<GrainReferenceKeyStringConverter>();
            services.TryAddSingleton<IGrainReferenceRuntime, GrainReferenceRuntime>();
            services.TryAddSingleton<GrainInterfaceTypeResolver>();
            services.TryAddSingleton<GrainPropertiesResolver>();
            services.TryAddSingleton<TypeConverter>();
            services.TryAddSingleton<IGrainCancellationTokenRuntime, GrainCancellationTokenRuntime>();
            services.TryAddFromExisting<IGrainFactory, GrainFactory>();
            services.TryAddFromExisting<IInternalGrainFactory, GrainFactory>();
            services.TryAddSingleton<ClientProviderRuntime>();
            services.TryAddSingleton<MessageFactory>();
            services.TryAddFromExisting<IStreamProviderRuntime, ClientProviderRuntime>();
            services.TryAddFromExisting<IProviderRuntime, ClientProviderRuntime>();
            services.TryAddSingleton<IStreamSubscriptionManagerAdmin, StreamSubscriptionManagerAdmin>();
            services.TryAddSingleton<ImplicitStreamSubscriberTable>();
            services.AddSingleton<IStreamNamespacePredicateProvider, DefaultStreamNamespacePredicateProvider>();
            services.AddSingleton<IStreamNamespacePredicateProvider, ConstructorStreamNamespacePredicateProvider>();
            services.AddSingletonKeyedService<string, IStreamIdMapper, DefaultStreamIdMapper>(DefaultStreamIdMapper.Name);
            services.TryAddSingleton<IInternalClusterClient, ClusterClient>();
            services.TryAddFromExisting<IClusterClient, IInternalClusterClient>();

            // Serialization
            services.TryAddSingleton<SerializationManager>(sp => ActivatorUtilities.CreateInstance<SerializationManager>(sp,
                sp.GetRequiredService<IOptions<ClientMessagingOptions>>().Value.LargeMessageWarningThreshold));
            services.TryAddSingleton<ITypeResolver, CachedTypeResolver>();
            services.TryAddSingleton<IFieldUtils, FieldUtils>();

            // Register the ISerializable serializer first, so that it takes precedence
            services.AddSingleton<DotNetSerializableSerializer>();
            services.AddFromExisting<IKeyedSerializer, DotNetSerializableSerializer>();

            services.TryAddSingleton<ILBasedSerializer>();
            services.AddFromExisting<IKeyedSerializer, ILBasedSerializer>();

            // Application parts
            var parts = builder.GetApplicationPartManager();
            services.TryAddSingleton<IApplicationPartManager>(parts);
            parts.AddApplicationPart(new AssemblyPart(typeof(RuntimeVersion).Assembly) { IsFrameworkAssembly = true });
            parts.AddFeatureProvider(new BuiltInTypesSerializationFeaturePopulator());
            parts.AddFeatureProvider(new AssemblyAttributeFeatureProvider<GrainInterfaceFeature>());
            parts.AddFeatureProvider(new AssemblyAttributeFeatureProvider<SerializerFeature>());
            services.AddTransient<IConfigurationValidator, ApplicationPartValidator>();

            services.TryAddSingleton(typeof(IKeyedServiceCollection<,>), typeof(KeyedServiceCollection<,>));

            // Add default option formatter if none is configured, for options which are required to be configured 
            services.ConfigureFormatter<ClusterOptions>();
            services.ConfigureFormatter<ClientMessagingOptions>();
            services.ConfigureFormatter<ConnectionOptions>();
            services.ConfigureFormatter<StatisticsOptions>();

            services.AddTransient<IConfigurationValidator, ClusterOptionsValidator>();
            services.AddTransient<IConfigurationValidator, ClientClusteringValidator>();

            // TODO: abstract or move into some options.
            services.AddSingleton<SocketSchedulers>();
            services.AddSingleton<SharedMemoryPool>();

            // Networking
            services.TryAddSingleton<ConnectionCommon>();
            services.TryAddSingleton<ConnectionManager>();
            services.AddSingleton<ILifecycleParticipant<IClusterClientLifecycle>, ConnectionManagerLifecycleAdapter<IClusterClientLifecycle>>();

            services.AddSingletonKeyedService<object, IConnectionFactory>(
                ClientOutboundConnectionFactory.ServicesKey,
                (sp, key) => ActivatorUtilities.CreateInstance<SocketConnectionFactory>(sp));

            services.TryAddTransient<IMessageSerializer>(sp => ActivatorUtilities.CreateInstance<MessageSerializer>(sp,
                sp.GetRequiredService<IOptions<ClientMessagingOptions>>().Value.MaxMessageHeaderSize,
                sp.GetRequiredService<IOptions<ClientMessagingOptions>>().Value.MaxMessageBodySize));
            services.TryAddSingleton<ConnectionFactory, ClientOutboundConnectionFactory>();
            services.TryAddSingleton<ClientMessageCenter>(sp => sp.GetRequiredService<OutsideRuntimeClient>().MessageCenter);
            services.TryAddFromExisting<IMessageCenter, ClientMessageCenter>();
            services.AddSingleton<GatewayManager>();
            services.AddSingleton<NetworkingTrace>();
            services.AddSingleton<MessagingTrace>();

            // Type metadata
            services.AddSingleton<ClientClusterManifestProvider>();
            services.AddFromExisting<IClusterManifestProvider, ClientClusterManifestProvider>();
            services.AddSingleton<ClientManifestProvider>();
            services.AddSingleton<IGrainInterfaceTypeProvider, AttributeGrainInterfaceTypeProvider>();
            services.AddSingleton<GrainTypeResolver>();
            services.AddSingleton<IGrainTypeProvider, AttributeGrainTypeProvider>();
            services.AddSingleton<IGrainTypeProvider, LegacyGrainTypeResolver>();
            services.AddSingleton<GrainPropertiesResolver>();
            services.AddSingleton<GrainInterfaceTypeResolver>();
            services.AddSingleton<IGrainInterfacePropertiesProvider, AttributeGrainInterfacePropertiesProvider>();
            services.AddSingleton<IGrainPropertiesProvider, AttributeGrainPropertiesProvider>();
            services.AddSingleton<IGrainInterfacePropertiesProvider, TypeNameGrainPropertiesProvider>();
            services.AddSingleton<IGrainPropertiesProvider, TypeNameGrainPropertiesProvider>();
            services.AddSingleton<IGrainPropertiesProvider, ImplementedInterfaceProvider>();
            // Logging helpers
            services.AddSingleton<ClientLoggingHelper>();
            services.AddFromExisting<ILifecycleParticipant<IClusterClientLifecycle>, ClientLoggingHelper>();
            services.AddFromExisting<IGrainIdLoggingHelper, ClientLoggingHelper>();
            services.AddFromExisting<IInvokeMethodRequestLoggingHelper, ClientLoggingHelper>();
        }
    }
}
