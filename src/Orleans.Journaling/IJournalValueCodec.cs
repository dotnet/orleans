using System.Buffers;
using Orleans.Serialization.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Provides serialization and deserialization of values for use in durable state journal entries.
/// </summary>
/// <typeparam name="T">The type of value to serialize and deserialize.</typeparam>
/// <remarks>
/// <para>
/// This interface is the primary extension point for customizing how values are serialized
/// in durable states. Unlike <c>IFieldCodec&lt;T&gt;</c> from Orleans.Serialization,
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
/// services.AddSingleton&lt;IJournalValueCodec&lt;MyEntity&gt;, MyEntityCodec&gt;();
///
/// // Register an open-generic codec for all types (e.g., JSON-based)
/// services.AddSingleton(typeof(IJournalValueCodec&lt;&gt;), typeof(MyJournalValueCodec&lt;&gt;));
/// </code>
/// </example>
/// </remarks>
public interface IJournalValueCodec<T>
{
    /// <summary>
    /// Serializes <paramref name="value"/> to the provided <paramref name="output"/> buffer.
    /// </summary>
    /// <param name="value">The value to serialize.</param>
    /// <param name="output">The buffer writer to write the serialized bytes to.</param>
    void Write(T value, IBufferWriter<byte> output);

    /// <summary>
    /// Deserializes a value of type <typeparamref name="T"/> from the provided <paramref name="reader"/>.
    /// </summary>
    /// <typeparam name="TInput">The Orleans serializer reader input type.</typeparam>
    /// <param name="reader">The reader positioned at the start of the encoded value. Implementations advance the reader past the value's bytes.</param>
    /// <returns>The deserialized value.</returns>
    T Read<TInput>(ref Reader<TInput> reader);
}
