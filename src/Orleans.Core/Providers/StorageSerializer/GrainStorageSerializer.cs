using System;

namespace Orleans.Storage
{
    /// <summary>
    /// Provides functionality for serializing and deserializing grain state, delegating to a prefered and fallback implementation of <see cref="IGrainStorageSerializer"/>.
    /// </summary>
    public class GrainStorageSerializer : IGrainStorageSerializer
    {
        private readonly IGrainStorageSerializer _serializer;
        private readonly IGrainStorageSerializer _fallbackDeserializer;

        /// <summary>
        /// Initializes a new instance of the <see cref="GrainStorageSerializer"/> class.
        /// </summary>
        /// <param name="serializer">The grain storage serializer.</param>
        /// <param name="fallbackDeserializer">The fallback grain storage serializer.</param>
        public GrainStorageSerializer(IGrainStorageSerializer serializer, IGrainStorageSerializer fallbackDeserializer)
        {
            _serializer = serializer;
            _fallbackDeserializer = fallbackDeserializer;
        }

        /// <inheritdoc/>
        public BinaryData Serialize<T>(T input) => _serializer.Serialize(input);

        /// <inheritdoc/>
        public T Deserialize<T>(BinaryData input)
        {
            try
            {
                return _serializer.Deserialize<T>(input);
            }
            catch (Exception ex1)
            {
                try
                {
                    return _fallbackDeserializer.Deserialize<T>(input);
                }
                catch (Exception ex2)
                {
                    throw new AggregateException("Failed to deserialize input", ex1, ex2);
                }
            }
        }

    }
}
