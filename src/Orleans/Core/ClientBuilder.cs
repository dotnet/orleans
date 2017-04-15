using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Orleans.CodeGeneration;
using Orleans.Messaging;
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
        private readonly List<Action<IServiceCollection>> configureServicesDelegates = new List<Action<IServiceCollection>>();
        private bool built;
        private ClientConfiguration clientConfiguration;

        /// <inheritdoc />
        public IClusterClient Build()
        {
            if (this.built) throw new InvalidOperationException($"{nameof(this.Build)} may only be called once per {nameof(ClientBuilder)} instance.");
            this.built = true;

            // If no configuration has been specified, use a default instance.
            this.clientConfiguration = this.clientConfiguration ?? ClientConfiguration.StandardLoad();
            
            // Configure the container.
            var services = new ServiceCollection();
            AddBasicServices(services, this.clientConfiguration);
            foreach (var configureServices in this.configureServicesDelegates)
            {
                configureServices(services);
            }

            // Build the container and configure the runtime client.
            var serviceProvider = services.BuildServiceProvider();
            serviceProvider.GetRequiredService<OutsideRuntimeClient>().ConsumeServices(serviceProvider);

            // Construct and return the cluster client.
            var result = serviceProvider.GetRequiredService<IClusterClient>();
            return result;
        }

        /// <inheritdoc />
        public IClientBuilder UseConfiguration(ClientConfiguration configuration)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            if (this.clientConfiguration != null) throw new InvalidOperationException("Base configuration has already been specified and cannot be overridden.");
            this.clientConfiguration = configuration;
            return this;
        }

        /// <inheritdoc />
        public IClientBuilder ConfigureServices(Action<IServiceCollection> configureServices)
        {
            if (configureServices == null) throw new ArgumentNullException(nameof(configureServices));
            this.configureServicesDelegates.Add(configureServices);
            return this;
        }

        private static void AddBasicServices(IServiceCollection services, ClientConfiguration clientConfiguration)
        {
            services.AddSingleton(clientConfiguration);
            services.AddSingleton<TypeMetadataCache>();
            services.AddSingleton<AssemblyProcessor>();
            services.AddSingleton<OutsideRuntimeClient>();
            services.AddFromExisting<IRuntimeClient, OutsideRuntimeClient>();
            services.AddFromExisting<IClusterConnectionStatusListener, OutsideRuntimeClient>();
            services.AddSingleton<GrainFactory>();
            services.AddFromExisting<IGrainFactory, GrainFactory>();
            services.AddFromExisting<IInternalGrainFactory, GrainFactory>();
            services.AddFromExisting<IGrainReferenceConverter, GrainFactory>();
            services.AddSingleton<ClientProviderRuntime>();
            services.AddFromExisting<IMessagingConfiguration, ClientConfiguration>();
            services.AddFromExisting<ITraceConfiguration, ClientConfiguration>();
            services.AddSingleton<IGatewayListProvider>(
                sp => ActivatorUtilities.CreateInstance<GatewayProviderFactory>(sp).CreateGatewayListProvider());
            services.AddSingleton<SerializationManager>();
            services.AddSingleton<MessageFactory>();
            services.AddSingleton<Factory<string, Logger>>(LogManager.GetLogger);
            services.AddSingleton<StreamProviderManager>();
            services.AddSingleton<ClientStatisticsManager>();
            services.AddFromExisting<IStreamProviderManager, StreamProviderManager>();
            services.AddFromExisting<IStreamProviderRuntime, ClientProviderRuntime>();
            services.AddSingleton<IStreamSubscriptionManagerAdmin, StreamSubscriptionManagerAdmin>();
            services.AddSingleton<CodeGeneratorManager>();
            services.AddSingleton<IInternalClusterClient, ClusterClient>();
            services.AddFromExisting<IClusterClient, IInternalClusterClient>();
        }
    }
}