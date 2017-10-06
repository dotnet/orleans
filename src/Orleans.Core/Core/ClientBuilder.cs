using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.ApplicationParts;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Messaging;
using Orleans.Metadata;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Serialization;
using Orleans.Streams;
using Orleans.Streams.Core;

namespace Orleans
{
    /// <summary>
    /// Builder used for creating <see cref="IClusterClient"/> instances.
    /// </summary>
    public class ClientBuilder : IClientBuilder
    {
        private readonly ServiceProviderBuilder serviceProviderBuilder = new ServiceProviderBuilder();
        private bool built;
        private bool clientConfigurationRegistered;
        
        /// <inheritdoc />
        public IDictionary<object, object> Properties { get; } = new Dictionary<object, object>();

        /// <inheritdoc />
        public IClusterClient Build()
        {
            if (this.built) throw new InvalidOperationException($"{nameof(this.Build)} may only be called once per {nameof(ClientBuilder)} instance.");
            this.built = true;

            // Configure default services and build the container.
            if (!this.clientConfigurationRegistered)
            {
                this.UseConfiguration(ClientConfiguration.StandardLoad());
            }

            this.serviceProviderBuilder.ConfigureServices(AddDefaultServices);
           
            var serviceProvider = this.serviceProviderBuilder.BuildServiceProvider(new HostBuilderContext(this.Properties));

            serviceProvider.GetRequiredService<OutsideRuntimeClient>().ConsumeServices(serviceProvider);

            // Construct and return the cluster client.
            return serviceProvider.GetRequiredService<IClusterClient>();
        }

        /// <inheritdoc />
        public IClientBuilder UseConfiguration(ClientConfiguration configuration)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            if (this.clientConfigurationRegistered) throw new InvalidOperationException("Base configuration has already been specified and cannot be overridden.");
            this.clientConfigurationRegistered = true;

            this.serviceProviderBuilder.ConfigureServices((context, services) => services.AddLegacyClientConfigurationSupport(configuration));
            return this;
        }

        /// <inheritdoc />
        public IClientBuilder ConfigureServices(Action<IServiceCollection> configureServices)
        {
            if (configureServices == null) throw new ArgumentNullException(nameof(configureServices));
            this.serviceProviderBuilder.ConfigureServices((context, services) => configureServices(services));
            return this;
        }

        /// <inheritdoc />
        public IClientBuilder UseServiceProviderFactory<TContainerBuilder>(IServiceProviderFactory<TContainerBuilder> factory)
        {
            this.serviceProviderBuilder.UseServiceProviderFactory(factory);
            return this;
        }

        /// <inheritdoc />
        public IClientBuilder ConfigureContainer<TContainerBuilder>(Action<TContainerBuilder> configureContainer)
        {
            this.serviceProviderBuilder.ConfigureContainer((HostBuilderContext context, TContainerBuilder containerBuilder) => configureContainer(containerBuilder));
            return this;
        }

        private static void AddDefaultServices(HostBuilderContext context, IServiceCollection services)
        {
            services.TryAddSingleton<TelemetryManager>();
            services.TryAddFromExisting<ITelemetryProducer, TelemetryManager>();
			services.AddLogging();
            //temporary change until runtime moved away from Logger
            services.TryAddSingleton(typeof(LoggerWrapper<>));
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
            services.TryAddSingleton<SerializationManager>();
            services.TryAddSingleton<MessageFactory>();
            services.TryAddSingleton<StreamProviderManager>();
            services.TryAddSingleton<ClientStatisticsManager>();
            services.TryAddFromExisting<IStreamProviderManager, StreamProviderManager>();
            services.TryAddFromExisting<IStreamProviderRuntime, ClientProviderRuntime>();
            services.TryAddFromExisting<IProviderRuntime, ClientProviderRuntime>();
            services.TryAddSingleton<IStreamSubscriptionManagerAdmin, StreamSubscriptionManagerAdmin>();
            services.TryAddSingleton<IInternalClusterClient, ClusterClient>();
            services.TryAddFromExisting<IClusterClient, IInternalClusterClient>();
            services.TryAddSingleton<ITypeResolver, CachedTypeResolver>();

            // Application parts
            var parts = context.GetApplicationPartManager();
            services.TryAddSingleton<ApplicationPartManager>(parts);
            parts.AddApplicationPart(typeof(RuntimeVersion).Assembly);
            parts.AddFeatureProvider(new BuiltInTypesSerializationFeaturePopulator());
            parts.AddFeatureProvider(new AssemblyAttributeFeatureProvider<GrainInterfaceFeature>());
            parts.AddFeatureProvider(new AssemblyAttributeFeatureProvider<SerializerFeature>());
        }
    }
}