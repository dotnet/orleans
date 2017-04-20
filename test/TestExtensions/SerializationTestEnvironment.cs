using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Serialization;

namespace TestExtensions
{
    public class SerializationTestEnvironment : IDisposable
    {
        public SerializationTestEnvironment(ClientConfiguration config = null)
        {
            if (config == null) config = this.DefaultConfig();
            this.Client = new ClientBuilder().UseConfiguration(config).Build();
            this.RuntimeClient = this.Client.ServiceProvider.GetRequiredService<OutsideRuntimeClient>();
        }

        public IClusterClient Client { get; set; }

        private ClientConfiguration DefaultConfig()
        {
            var result = new ClientConfiguration();
            MixinDefaults(result);
            return result;
        }

        private static void MixinDefaults(ClientConfiguration config)
        {
            if (config.GatewayProvider == ClientConfiguration.GatewayProviderType.None)
            {
                config.GatewayProvider = ClientConfiguration.GatewayProviderType.Config;
                config.Gateways.Add(new IPEndPoint(0, 0));
            }
            config.TraceToConsole = false;
        }

        internal OutsideRuntimeClient RuntimeClient { get; set; }

        public static SerializationTestEnvironment InitializeWithDefaults(ClientConfiguration config = null)
        {
            config = config ?? new ClientConfiguration();
            MixinDefaults(config);

            var result = new SerializationTestEnvironment(config);
            return result;
        }

        public static SerializationTestEnvironment Initialize(List<TypeInfo> serializationProviders = null, TypeInfo fallbackProvider = null)
        {
            var config = new ClientConfiguration {FallbackSerializationProvider = fallbackProvider};
            if (serializationProviders != null) config.SerializationProviders.AddRange(serializationProviders);
            return InitializeWithDefaults(config);
        }
        
        public IGrainFactory GrainFactory => this.RuntimeClient.InternalGrainFactory;

        internal IInternalGrainFactory InternalGrainFactory => this.RuntimeClient.InternalGrainFactory;

        internal IServiceProvider Services => this.RuntimeClient.ServiceProvider;

        public SerializationManager SerializationManager => this.RuntimeClient.SerializationManager;
        
        public void Dispose()
        {
            this.RuntimeClient?.Dispose();
        }
    }
}