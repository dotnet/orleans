using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.ApplicationParts;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Metadata;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streams;
using Orleans.Streams.Core;

namespace Orleans
{
    internal static class DefaultClientServices
    {
        public static void AddDefaultServices(IClientBuilder builder, IServiceCollection services)
        {
            services.TryAddSingleton<TelemetryManager>();
            services.TryAddFromExisting<ITelemetryProducer, TelemetryManager>();
            services.AddLogging();
            //temporary change until runtime moved away from Logger
            services.TryAddSingleton(typeof(LoggerWrapper<>));
            services.TryAddSingleton<ExecutorService>();
            services.TryAddSingleton<LoadedProviderTypeLoaders>();
            services.TryAddSingleton<StatisticsProviderManager>();
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
            services.TryAddSingleton<StreamProviderManager>();
            services.TryAddSingleton<ClientStatisticsManager>();
            services.TryAddFromExisting<IStreamProviderManager, StreamProviderManager>();
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
            services.TryAddSingleton<ApplicationPartManager>(parts);
            parts.AddApplicationPart(new AssemblyPart(typeof(RuntimeVersion).Assembly) { IsFrameworkAssembly = true });
            parts.AddFeatureProvider(new BuiltInTypesSerializationFeaturePopulator());
            parts.AddFeatureProvider(new AssemblyAttributeFeatureProvider<GrainInterfaceFeature>());
            parts.AddFeatureProvider(new AssemblyAttributeFeatureProvider<SerializerFeature>());
            services.AddTransient<IConfigurationValidator, ApplicationPartValidator>();
        }
    }
}