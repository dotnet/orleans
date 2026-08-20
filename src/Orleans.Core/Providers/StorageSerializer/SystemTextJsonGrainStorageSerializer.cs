#nullable enable

using System;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Orleans.Serialization;

namespace Orleans.Storage
{
    /// <summary>
    /// Grain storage serializer for System.Text.Json.
    /// </summary>
    /// <param name="options">The serializer options.</param>
    public sealed class SystemTextJsonGrainStorageSerializer(IOptions<SystemTextJsonGrainStorageSerializerOptions> options) : IGrainStorageStreamingSerializer
    {
        /// <inheritdoc/>
        public T? Deserialize<T>(BinaryData input) => input.ToObjectFromJson<T>(options.Value.JsonSerializerOptions);

        /// <inheritdoc/>
        public BinaryData Serialize<T>(T? input) => BinaryData.FromObjectAsJson(input, options.Value.JsonSerializerOptions);

        /// <inheritdoc/>
        public ValueTask SerializeAsync<T>(T? input, Stream destination, CancellationToken cancellationToken = default)
            => new(JsonSerializer.SerializeAsync(destination, input, options.Value.JsonSerializerOptions, cancellationToken));

        /// <inheritdoc/>
        public async ValueTask<T?> DeserializeAsync<T>(Stream input, CancellationToken cancellationToken = default)
            => await JsonSerializer.DeserializeAsync<T>(input, options.Value.JsonSerializerOptions, cancellationToken).ConfigureAwait(false);
    }
}