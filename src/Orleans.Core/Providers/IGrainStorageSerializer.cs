using System;

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
}
