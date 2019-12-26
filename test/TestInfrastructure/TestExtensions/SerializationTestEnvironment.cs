using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Configuration;
using Orleans.Serialization;

namespace TestExtensions
{
    public class SerializationTestEnvironment : IDisposable
    {
        public SerializationTestEnvironment(Action<IClientBuilder> configureClientBuilder = null)
        {
            var builder = new ClientBuilder()
                .ConfigureDefaults()
                .UseLocalhostClustering();
            configureClientBuilder?.Invoke(builder);
            this.Client = builder.Build();
            this.RuntimeClient = this.Client.ServiceProvider.GetRequiredService<OutsideRuntimeClient>();
        }

        public IClusterClient Client { get; set; }
        
        internal OutsideRuntimeClient RuntimeClient { get; set; }

        public static SerializationTestEnvironment InitializeWithDefaults(Action<IClientBuilder> configureClientBuilder = null)
        {
            var result = new SerializationTestEnvironment(configureClientBuilder);
            return result;
        }

        public static SerializationTestEnvironment Initialize(List<Type> serializationProviders = null, Type fallbackProvider = null)
        {
            return InitializeWithDefaults(clientBuilder => clientBuilder.Configure<SerializationProviderOptions>(options =>
            {
                options.FallbackSerializationProvider = fallbackProvider;
                if (serializationProviders != null)
                {
                    options.SerializationProviders.AddRange(serializationProviders);
                }
            }));
        }
        
        public IGrainFactory GrainFactory => this.RuntimeClient.InternalGrainFactory;

        internal IInternalGrainFactory InternalGrainFactory => this.RuntimeClient.InternalGrainFactory;

        internal IServiceProvider Services => this.Client.ServiceProvider;

        public SerializationManager SerializationManager => this.RuntimeClient.ServiceProvider.GetRequiredService<SerializationManager>();
        
        public void Dispose()
        {
            this.RuntimeClient?.Dispose();
        }
    }
}