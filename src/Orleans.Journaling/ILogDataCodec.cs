using System.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Provides serialization and deserialization of values for use in durable state machine log entries.
/// </summary>
/// <typeparam name="T">The type of value to serialize and deserialize.</typeparam>
/// <remarks>
/// <para>
/// This interface is the primary extension point for customizing how values are serialized
/// in durable state machines. Unlike <c>IFieldCodec&lt;T&gt;</c> from Orleans.Serialization,
/// this interface does not require serializer sessions or field headers — it is a simple
/// bytes-in/bytes-out contract.
/// </para>
/// <para>
/// Implementations are registered in dependency injection as open-generic or closed-generic
/// services. For example:
/// </para>
/// <example>
/// <code>
/// // Register a custom codec for a specific type
/// services.AddSingleton&lt;ILogDataCodec&lt;MyEntity&gt;, MyEntityCodec&gt;();
///
/// // Register an open-generic codec for all types (e.g., JSON-based)
/// services.AddSingleton(typeof(ILogDataCodec&lt;&gt;), typeof(JsonLogDataCodec&lt;&gt;));
/// </code>
/// </example>
/// </remarks>
public interface ILogDataCodec<T>
{
    /// <summary>
    /// Serializes <paramref name="value"/> to the provided <paramref name="output"/> buffer.
    /// </summary>
    /// <param name="value">The value to serialize.</param>
    /// <param name="output">The buffer writer to write the serialized bytes to.</param>
    void Write(T value, IBufferWriter<byte> output);

    /// <summary>
    /// Deserializes a value of type <typeparamref name="T"/> from the provided <paramref name="input"/> buffer.
    /// </summary>
    /// <param name="input">The buffer containing the serialized data.</param>
    /// <param name="bytesConsumed">The number of bytes consumed from <paramref name="input"/>.</param>
    /// <returns>The deserialized value.</returns>
    T Read(ReadOnlySequence<byte> input, out long bytesConsumed);
}
