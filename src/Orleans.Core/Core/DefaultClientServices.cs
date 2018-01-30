using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.ApplicationParts;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Metadata;
using Orleans.Providers;
using Orleans.Runtime;
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
            services.TryAddSingleton<ILifecycleParticipant<IClusterClientLifecycle>, ClientOptionsLogger>();
            services.TryAddSingleton<TelemetryManager>();
            services.TryAddFromExisting<ITelemetryProducer, TelemetryManager>();
            services.TryAddSingleton<IHostEnvironmentStatistics, NoOpHostEnvironmentStatistics>();
            services.TryAddSingleton<IAppEnvironmentStatistics, AppEnvironmentStatistics>();
            services.AddLogging();
            services.TryAddSingleton<ExecutorService>();
            services.TryAddSingleton<TypeMetadataCache>();
            services.TryAddSingleton<OutsideRuntimeClient>();
            services.TryAddFromExisting<IRuntimeClient, OutsideRuntimeClient>();
            services.TryAddFromExisting<IClusterConnectionStatusListener, OutsideRuntimeClient>();
            services.TryAddSingleton<GrainFactory>();
            services.TryAddSingleton<IGrainReferenceRuntime, GrainReferenceRuntime>();
            services.TryAddSingleton<IGrainCancellationTokenRuntime, GrainCancellationTokenRuntime>();
            services.TryAddFromExisting<IGrainFactory, GrainFactory>();
            services.TryAddFromExisting<IInternalGrainFactory, GrainFactory>();
            services.TryAddFromExisting<IGrainReferenceConverter, GrainFactory>();
            services.TryAddSingleton<ClientProviderRuntime>();
            services.TryAddSingleton<MessageFactory>();
            services.TryAddSingleton<ClientStatisticsManager>();
            services.TryAddFromExisting<IStreamProviderRuntime, ClientProviderRuntime>();
            services.TryAddFromExisting<IProviderRuntime, ClientProviderRuntime>();
            services.TryAddSingleton<IStreamSubscriptionManagerAdmin, StreamSubscriptionManagerAdmin>();
            services.TryAddSingleton<IInternalClusterClient, ClusterClient>();
            services.TryAddFromExisting<IClusterClient, IInternalClusterClient>();

            // Serialization
            services.TryAddSingleton<SerializationManager>();
            services.TryAddSingleton<ITypeResolver, CachedTypeResolver>();
            services.TryAddSingleton<IFieldUtils, FieldUtils>();
            services.AddSingleton<BinaryFormatterSerializer>();
            services.AddSingleton<BinaryFormatterISerializableSerializer>();
            services.AddFromExisting<IKeyedSerializer, BinaryFormatterISerializableSerializer>();
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

            //Add default option formatter if none is configured, for options which are requied to be configured 
            services.TryConfigureFormatter<ClusterClientOptions, ClusterClientOptionsFormatter>();
            services.TryConfigureFormatter<ClientMessagingOptions, ClientMessagingOptionFormatter>();
            services.TryConfigureFormatter<NetworkingOptions, NetworkingOptionFormatter>();
            services.TryConfigureFormatter<StatisticsOptions, StatisticOptionsFormatter>();
        }
    }
}