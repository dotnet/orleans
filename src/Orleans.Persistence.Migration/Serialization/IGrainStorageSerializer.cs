using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace Orleans.Storage
{
    /// <summary>
    /// Common interface for grain state serializers.
    /// </summary>
    public interface IGrainStorageSerializer
    {
        /// <summary>
        /// Serializes the object input.
        /// </summary>
        /// <param name="input">The object to serialize.</param>
        /// <typeparam name="T">The input type.</typeparam>
        /// <returns>The serialized input.</returns>
        BinaryData Serialize<T>(T input);

        /// <summary>
        /// Deserializes the provided data.
        /// </summary>
        /// <param name="input">The data to deserialize.</param>
        /// <typeparam name="T">The output type.</typeparam>
        /// <returns>The deserialized object.</returns>
        T Deserialize<T>(BinaryData input);
    }

    /// <summary>
    /// Extensions for <see cref="IGrainStorageSerializer"/>.
    /// </summary>
    public static class GrainStorageSerializerExtensions
    {
        /// <summary>
        /// Deserializes the provided data.
        /// </summary>
        /// <param name="serializer">The grain state serializer.</param>
        /// <param name="input">The data to deserialize.</param>
        /// <typeparam name="T">The output type.</typeparam>
        /// <returns>The deserialized object.</returns>
        public static T Deserialize<T>(this IGrainStorageSerializer serializer, ReadOnlyMemory<byte> input)
            => serializer.Deserialize<T>(new BinaryData(input));
    }

    /// <summary>
    /// Interface to be implemented by the storage provider options.
    /// </summary>
    public interface IStorageProviderSerializerOptions
    {
        /// <summary>
        /// Gets or sets the serializer to use for this storage provider.
        /// </summary>
        IGrainStorageSerializer GrainStorageSerializer { get; set; }
    }

    /// <summary>
    /// Provides default configuration for <see cref="IStorageProviderSerializerOptions.GrainStorageSerializer"/>.
    /// </summary>
    /// <typeparam name="TOptions">The options type.</typeparam>
    public class DefaultStorageProviderSerializerOptionsConfigurator<TOptions> : IPostConfigureOptions<TOptions> where TOptions : class, IStorageProviderSerializerOptions
    {
        private readonly IServiceProvider _serviceProvider;

        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultStorageProviderSerializerOptionsConfigurator{TOptions}"/> class.
        /// </summary>
        /// <param name="serviceProvider">The service provider.</param>
        public DefaultStorageProviderSerializerOptionsConfigurator(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        /// <inheritdoc/>
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
