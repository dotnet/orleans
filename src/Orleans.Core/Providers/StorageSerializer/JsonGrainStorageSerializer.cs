using Orleans.Serialization;

namespace Orleans.Storage
{
    /// <summary>
    /// Grain storage serializer that uses Newtonsoft.Json
    /// </summary>
    public class JsonGrainStorageSerializer : IGrainStorageSerializer, IGrainStorageStreamingSerializer
    {
        private readonly OrleansJsonSerializer _orleansJsonSerializer;

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonGrainStorageSerializer"/> class.
        /// </summary>
        public JsonGrainStorageSerializer(OrleansJsonSerializer orleansJsonSerializer)
        {
            _orleansJsonSerializer = orleansJsonSerializer;
        }

        /// <inheritdoc/>
        public BinaryData Serialize<T>(T value)
        {
            var data = _orleansJsonSerializer.Serialize(value, typeof(T));
            return new BinaryData(data);
        }

        /// <inheritdoc/>
        public T Deserialize<T>(BinaryData input)
        {
            return (T)_orleansJsonSerializer.Deserialize(typeof(T), input.ToString());
        }

        /// <inheritdoc/>
        public ValueTask SerializeAsync<T>(T value, Stream destination, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _orleansJsonSerializer.Serialize(value, typeof(T), destination);
            return ValueTask.CompletedTask;
        }

        /// <inheritdoc/>
        public ValueTask<T> DeserializeAsync<T>(Stream input, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult((T)_orleansJsonSerializer.Deserialize(typeof(T), input));
        }
    }
}
