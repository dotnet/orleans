using System;
using System.IO;


namespace Orleans.Storage
{
    /// <summary>
    /// A canonical interface for a storage provider deserializer.
    /// </summary>
    public interface IStorageDeserializer
    {
        /// <summary>
        /// Can this provider stream data.
        /// </summary>
        bool CanStream { get; }

        /// <summary>
        /// An optional tag that a <see cref="IStorageSerializationPicker"/> or <see cref="IStorageProvider"/> provider can use to pick a deserializer.
        /// </summary>
        string Tag { get; }

        /// <summary>
        /// Deserializes the given data.
        /// </summary>
        /// <param name="data">The data to be serialized.</param>
        /// <param name="grainStateType">The type of the grain state.</param>
        /// <returns>The deserialized object.</returns>
        object Deserialize(object data, Type grainStateType);

        /// <summary>
        /// Deserializes the given data from a stream.
        /// </summary>
        /// <param name="dataStream">The stream from which to serialize.</param>
        /// <param name="grainStateType">The type of the grain state.</param>
        /// <returns>The deserialized object.</returns>
        object Deserialize(Stream dataStream, Type grainStateType);

        /// <summary>
        /// Deserializes the given data from a text stream.
        /// </summary>
        /// <param name="reader">The text stream from which to serialize.</param>
        /// <param name="grainStateType">The type of the grain state.</param>
        /// <returns>The deserialized object.</returns>
        object Deserialize(TextReader reader, Type grainStateType);
    }
}
