using System.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Provides serialization and deserialization of log entries for a specific durable type.
/// </summary>
/// <typeparam name="TEntry">The base entry type for the durable type's command hierarchy.</typeparam>
/// <remarks>
/// <para>
/// Each durable type has its own entry codec that handles serialization of all command
/// variants for that type. For example, <c>ILogEntryCodec&lt;DurableDictionaryEntry&lt;K, V&gt;&gt;</c>
/// handles Set, Remove, Clear, and Snapshot entries.
/// </para>
/// <para>
/// Implementations exist for each serialization format:
/// </para>
/// <list type="bullet">
/// <item><description>Orleans binary — preserves the legacy wire format</description></item>
/// <item><description>JSON — uses <c>System.Text.Json</c> with polymorphic <c>"cmd"</c> discriminator</description></item>
/// <item><description>Protocol Buffers — uses <c>oneof</c> for command variants</description></item>
/// </list>
/// <example>
/// <code>
/// // Durable types use the codec to serialize/deserialize entries:
/// _entryCodec.Write(new DictionarySetEntry&lt;string, int&gt;("key", 42), bufferWriter);
///
/// var entry = _entryCodec.Read(logEntry);
/// switch (entry)
/// {
///     case DictionarySetEntry&lt;string, int&gt;(var key, var value): ...
/// }
/// </code>
/// </example>
/// </remarks>
public interface ILogEntryCodec<TEntry>
{
    /// <summary>
    /// Serializes an entry to the provided buffer writer.
    /// </summary>
    /// <param name="entry">The entry to serialize.</param>
    /// <param name="output">The buffer writer to write the serialized bytes to.</param>
    void Write(TEntry entry, IBufferWriter<byte> output);

    /// <summary>
    /// Deserializes an entry from the provided buffer.
    /// </summary>
    /// <param name="input">The buffer containing the serialized data.</param>
    /// <returns>The deserialized entry.</returns>
    TEntry Read(ReadOnlySequence<byte> input);
}
