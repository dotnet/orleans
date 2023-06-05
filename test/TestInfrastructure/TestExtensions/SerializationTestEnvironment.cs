using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Serialization;

namespace TestExtensions
{
    public class SerializationTestEnvironment : IDisposable
    {
        public SerializationTestEnvironment(Action<IClientBuilder> configureClientBuilder = null)
        {
            var host = new HostBuilder()
                .UseOrleansClient((ctx, clientBuilder) =>
                {
                    clientBuilder.UseLocalhostClustering();
                    configureClientBuilder?.Invoke(clientBuilder);
                }).Build();

            Client = host.Services.GetRequiredService<IClusterClient>();
            RuntimeClient = Client.ServiceProvider.GetRequiredService<OutsideRuntimeClient>();
            RuntimeClient.ConsumeServices();
        }

        public IClusterClient Client { get; set; }
        
        internal OutsideRuntimeClient RuntimeClient { get; set; }

        public static SerializationTestEnvironment InitializeWithDefaults(Action<IClientBuilder> configureClientBuilder = null)
        {
            var result = new SerializationTestEnvironment(configureClientBuilder);
            return result;
        }
        
        public IGrainFactory GrainFactory => RuntimeClient.InternalGrainFactory;

        internal IInternalGrainFactory InternalGrainFactory => RuntimeClient.InternalGrainFactory;

        internal IServiceProvider Services => Client.ServiceProvider;

        public DeepCopier DeepCopier => RuntimeClient.ServiceProvider.GetRequiredService<DeepCopier>();
        public Serializer Serializer => RuntimeClient.ServiceProvider.GetRequiredService<Serializer>();

        public void Dispose() => RuntimeClient?.Dispose();
    }
}