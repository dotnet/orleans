using Orleans.Serialization.Buffers;
using System.Buffers;

namespace Orleans.Serialization.Serializers
{
    /// <summary>
    /// Functionality for serializing a value type.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    public interface IValueSerializer<T> : IValueSerializer where T : struct
    {
        /// <summary>
        /// Serializes the provided value.
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="value">The value.</param>
        void Serialize<TBufferWriter>(ref Writer<TBufferWriter> writer, scoped ref T value) where TBufferWriter : IBufferWriter<byte>;

        /// <summary>
        /// Deserializes the specified type.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <param name="value">The value.</param>
        void Deserialize<TInput>(ref Reader<TInput> reader, scoped ref T value);
    }

    /// <summary>
    /// Marker interface for value type serializers.
    /// </summary>
    public interface IValueSerializer
    {
    }
}