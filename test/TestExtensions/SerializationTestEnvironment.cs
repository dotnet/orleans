using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using Orleans;
using Orleans.Runtime.Configuration;

namespace TestExtensions
{
    public class SerializationTestEnvironment : IDisposable
    {
        public SerializationTestEnvironment(ClientConfiguration config = null)
        {
            if (config == null) config = this.DefaultConfig();
            this.RuntimeClient = new OutsideRuntimeClient(config, true);
        }

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

        public void Dispose()
        {
            this.RuntimeClient?.Dispose();
        }
    }
}