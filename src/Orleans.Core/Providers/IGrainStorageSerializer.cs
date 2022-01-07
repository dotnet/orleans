using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace Orleans.Storage
{
    /// <summary>
    /// Common interface for grain state serializers
    /// </summary>
    public interface IGrainStorageSerializer
    {
        /// <summary>
        /// Serialize the object in input
        /// </summary>
        /// <param name="input">Object to serialize</param>
        /// <returns>The serialized input</returns>
        BinaryData Serialize<T>(T input);

        /// <summary>
        /// Deserialize the data in input
        /// </summary>
        /// <param name="input">Data to deserialize</param>
        /// <returns>The deserialized object</returns>
        T Deserialize<T>(BinaryData input);
    }

    public static class GrainStorageSerializerExtensions
    {
        public static T Deserialize<T>(this IGrainStorageSerializer serializer, ReadOnlyMemory<byte> input)
            => serializer.Deserialize<T>(new BinaryData(input));
    }

    /// <summary>
    /// Interface to be implemented by the storage provider options
    /// </summary>
    public interface IStorageProviderSerializerOptions
    {
        /// <summary>
        /// Serializer to use for this provider
        /// </summary>
        public IGrainStorageSerializer GrainStorageSerializer { get; set; }
    }

    public class DefaultStorageProviderSerializerOptionsConfigurator<TOptions> : IPostConfigureOptions<TOptions> where TOptions : class, IStorageProviderSerializerOptions
    {
        private readonly IServiceProvider _serviceProvider;

        public DefaultStorageProviderSerializerOptionsConfigurator(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public void PostConfigure(string name, TOptions options)
        {
            if (options.GrainStorageSerializer == default)
            {
                // First, try to get a IGrainStorageSerializer that was registered with 
                // the same name as the storage provider
                // If none is found, fallback to system wide default
                options.GrainStorageSerializer = _serviceProvider.GetServiceByName<IGrainStorageSerializer>(name) ?? _serviceProvider.GetRequiredService<IGrainStorageSerializer>();
            }
        }
    }
}
