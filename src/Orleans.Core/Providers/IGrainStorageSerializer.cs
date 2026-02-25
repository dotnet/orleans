using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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

#nullable enable
    /// <summary>
    /// Optional stream-based serializer for grain state.
    /// </summary>
    public interface IGrainStorageStreamingSerializer
    {
        /// <summary>
        /// Serializes the object input to a stream.
        /// </summary>
        /// <param name="input">The object to serialize.</param>
        /// <param name="destination">The destination stream.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <typeparam name="T">The input type.</typeparam>
        ValueTask SerializeAsync<T>(T input, Stream destination, CancellationToken cancellationToken = default);

        /// <summary>
        /// Deserializes the provided data from a stream.
        /// </summary>
        /// <param name="input">The input stream.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <typeparam name="T">The output type.</typeparam>
        /// <returns>The deserialized object.</returns>
        ValueTask<T?> DeserializeAsync<T>(Stream input, CancellationToken cancellationToken = default);
    }
#nullable restore

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
                options.GrainStorageSerializer = _serviceProvider.GetKeyedService<IGrainStorageSerializer>(name) ?? _serviceProvider.GetRequiredService<IGrainStorageSerializer>();
            }
        }
    }
}
