#nullable enable

using System;
using Microsoft.Extensions.Options;
using Orleans.Serialization;
using Orleans.Storage;

namespace Orleans.Providers.StorageSerializer
{
    /// <summary>
    /// Grain storage serializer for System.Text.Json
    /// </summary>
    /// <param name="options"></param>
    public sealed class SystemTextJsonGrainStorageSerializer(IOptions<SystemTextJsonGrainStorageSerializerOptions> options) : IGrainStorageSerializer
    {
        /// <inheritdoc/>
        public T Deserialize<T>(BinaryData input) => input.ToObjectFromJson<T>(options.Value.JsonSerializerOptions)!;
        /// <inheritdoc/>
        public BinaryData Serialize<T>(T input) => BinaryData.FromObjectAsJson(input, options.Value.JsonSerializerOptions);
    }
}