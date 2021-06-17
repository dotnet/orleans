using System;
using System.Collections.Generic;
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
        
        public IGrainFactory GrainFactory => this.RuntimeClient.InternalGrainFactory;

        internal IInternalGrainFactory InternalGrainFactory => this.RuntimeClient.InternalGrainFactory;

        internal IServiceProvider Services => this.Client.ServiceProvider;

        public DeepCopier DeepCopier => this.RuntimeClient.ServiceProvider.GetRequiredService<DeepCopier>();
        public Serializer Serializer => RuntimeClient.ServiceProvider.GetRequiredService<Serializer>();
        
        public void Dispose()
        {
            this.RuntimeClient?.Dispose();
        }
    }
}