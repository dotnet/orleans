using System.IO;


namespace Orleans.Storage
{
    /// <summary>
    /// A canonical interface for a storage provider serializer.
    /// </summary>
    public interface IStorageSerializer
    {
        /// <summary>
        /// Can this provider stream data.
        /// </summary>
        bool CanStream { get; }

        /// <summary>
        /// An optional tag that a <see cref="IStorageSerializationPicker"/> or <see cref="IStorageProvider"/> provider can use to pick this serializer.
        /// </summary>
        string Tag { get; }

        /// <summary>
        /// Serializes the given data.
        /// </summary>
        /// <param name="data">The data to be serialized.</param>
        /// <returns>The serialized data.</returns>
        object Serialize(object data);

        /// <summary>
        /// Serializes the given data to a stream.
        /// </summary>
        /// <param name="dataStream">The stream to serialize to.</param>
        /// <param name="data">The data to serialize.</param>
        /// <returns>The stream to which data was serialized. The same as <paramref name="dataStream"/>.</returns>
        object Serialize(Stream dataStream, object data);


        /// <summary>
        /// Serializes the given data to a text stream.
        /// </summary>
        /// <param name="writer">The stream to serialize the text to.</param>
        /// <param name="data">The data to serialize.</param>
        /// <returns>The stream to which data was serialized. The same as <paramref name="writer"/>.</returns>
        object Serialize(TextWriter writer, object data);
    }
}
