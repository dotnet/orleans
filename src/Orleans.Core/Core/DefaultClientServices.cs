using Orleans.Configuration;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Orleans.Configuration.Internal;
using Orleans.Configuration.Validators;
using Orleans.GrainReferences;
using Orleans.Messaging;
using Orleans.Metadata;
using Orleans.Networking.Shared;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using Orleans.Runtime.Versions;
using Orleans.Serialization;
using Orleans.Statistics;
using Orleans.Serialization.TypeSystem;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Cloning;

namespace Orleans
{
    internal static class DefaultClientServices
    {
        public static void AddDefaultServices(IServiceCollection services)
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
            services.TryAddSingleton<ApplicationRequestsStatisticsGroup>();
            services.TryAddSingleton<StageAnalysisStatisticsGroup>();
            services.TryAddSingleton<SchedulerStatisticsGroup>();
            services.AddLogging();
            services.TryAddSingleton<GrainBindingsResolver>();
            services.TryAddSingleton<OutsideRuntimeClient>();
            services.TryAddSingleton<ClientGrainContext>();
            services.AddFromExisting<IGrainContextAccessor, ClientGrainContext>();
            services.TryAddFromExisting<IRuntimeClient, OutsideRuntimeClient>();
            services.TryAddFromExisting<IClusterConnectionStatusListener, OutsideRuntimeClient>();
            services.TryAddSingleton<GrainFactory>();
            services.TryAddSingleton<GrainInterfaceTypeToGrainTypeResolver>();
            services.TryAddSingleton<GrainReferenceActivator>();
            services.AddSingleton<IGrainReferenceActivatorProvider, GrainReferenceActivatorProvider>();
            services.AddSingleton<IGrainReferenceActivatorProvider, UntypedGrainReferenceActivatorProvider>();
            services.TryAddSingleton<NewRpcProvider>();
            services.TryAddSingleton<GrainReferenceKeyStringConverter>();
            services.TryAddSingleton<IGrainReferenceRuntime, GrainReferenceRuntime>();
            services.TryAddSingleton<GrainPropertiesResolver>();
            services.TryAddSingleton<IGrainCancellationTokenRuntime, GrainCancellationTokenRuntime>();
            services.TryAddFromExisting<IGrainFactory, GrainFactory>();
            services.TryAddFromExisting<IInternalGrainFactory, GrainFactory>();
            services.TryAddSingleton<ClientProviderRuntime>();
            services.TryAddSingleton<MessageFactory>();
            services.TryAddFromExisting<IProviderRuntime, ClientProviderRuntime>();
            services.TryAddSingleton<IInternalClusterClient, ClusterClient>();
            services.TryAddFromExisting<IClusterClient, IInternalClusterClient>();

            services.AddSingleton<IConfigureOptions<GrainTypeOptions>, DefaultGrainTypeOptionsProvider>();
            services.TryAddSingleton(typeof(IKeyedServiceCollection<,>), typeof(KeyedServiceCollection<,>));

            // Add default option formatter if none is configured, for options which are required to be configured 
            services.ConfigureFormatter<ClusterOptions>();
            services.ConfigureFormatter<ClientMessagingOptions>();
            services.ConfigureFormatter<ConnectionOptions>();
            services.ConfigureFormatter<StatisticsOptions>();

            services.AddTransient<IConfigurationValidator, GrainTypeOptionsValidator>();
            services.AddTransient<IConfigurationValidator, ClusterOptionsValidator>();
            services.AddTransient<IConfigurationValidator, ClientClusteringValidator>();

            // TODO: abstract or move into some options.
            services.AddSingleton<SocketSchedulers>();
            services.AddSingleton<SharedMemoryPool>();

            // Networking
            services.TryAddSingleton<ConnectionCommon>();
            services.TryAddSingleton<ConnectionManager>();
            services.TryAddSingleton<ConnectionPreambleHelper>();
            services.AddSingleton<ILifecycleParticipant<IClusterClientLifecycle>, ConnectionManagerLifecycleAdapter<IClusterClientLifecycle>>();

            services.AddSingletonKeyedService<object, IConnectionFactory>(
                ClientOutboundConnectionFactory.ServicesKey,
                (sp, key) => ActivatorUtilities.CreateInstance<SocketConnectionFactory>(sp));

            services.AddSerializer();
            services.AddSingleton<ITypeFilter, AllowOrleansTypes>();
            services.AddSingleton<ISpecializableCodec, GrainReferenceCodecProvider>();
            services.AddSingleton<ISpecializableCopier, GrainReferenceCopierProvider>();
            services.AddSingleton<OnDeserializedCallbacks>();

            services.TryAddTransient<IMessageSerializer>(sp => ActivatorUtilities.CreateInstance<MessageSerializer>(
                sp,
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
            services.AddSingleton<GrainPropertiesResolver>();
            services.AddSingleton<GrainVersionManifest>();
            services.AddSingleton<GrainInterfaceTypeResolver>();
            services.AddSingleton<IGrainInterfacePropertiesProvider, AttributeGrainInterfacePropertiesProvider>();
            services.AddSingleton<IGrainPropertiesProvider, AttributeGrainPropertiesProvider>();
            services.AddSingleton<IGrainInterfacePropertiesProvider, TypeNameGrainPropertiesProvider>();
            services.AddSingleton<IGrainPropertiesProvider, TypeNameGrainPropertiesProvider>();
            services.AddSingleton<IGrainPropertiesProvider, ImplementedInterfaceProvider>();
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
