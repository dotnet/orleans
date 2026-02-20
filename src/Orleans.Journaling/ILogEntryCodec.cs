using System.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Writes structured data fields to a log entry in a format-specific way.
/// </summary>
/// <remarks>
/// <para>
/// Implementations of this interface encode log entry commands and their associated data
/// in a specific format (e.g., Orleans binary, JSON, Protocol Buffers).
/// </para>
/// <para>
/// Durable types use this interface to write log entries without being coupled to a specific
/// serialization format. After writing all fields, call <see cref="WriteTo"/> to flush the
/// encoded data to the output buffer.
/// </para>
/// </remarks>
public interface ILogEntryWriter : IDisposable
{
    /// <summary>
    /// Writes a command identifier.
    /// </summary>
    /// <param name="command">The command type value.</param>
    void WriteCommand(uint command);

    /// <summary>
    /// Writes an unsigned 32-bit integer value.
    /// </summary>
    /// <param name="value">The value to write.</param>
    void WriteUInt32(uint value);

    /// <summary>
    /// Writes an unsigned 64-bit integer value.
    /// </summary>
    /// <param name="value">The value to write.</param>
    void WriteUInt64(ulong value);

    /// <summary>
    /// Writes a single byte value.
    /// </summary>
    /// <param name="value">The byte to write.</param>
    void WriteByte(byte value);

    /// <summary>
    /// Writes a value of type <typeparamref name="T"/> using the provided codec.
    /// </summary>
    /// <typeparam name="T">The type of value to write.</typeparam>
    /// <param name="codec">The codec to use for serialization.</param>
    /// <param name="value">The value to write.</param>
    void WriteValue<T>(ILogDataCodec<T> codec, T value);

    /// <summary>
    /// Flushes the encoded data to the provided output buffer.
    /// </summary>
    /// <param name="output">The buffer writer to write the encoded data to.</param>
    void WriteTo(IBufferWriter<byte> output);
}

/// <summary>
/// Reads structured data fields from a log entry in a format-specific way.
/// </summary>
/// <remarks>
/// Implementations of this interface decode log entry commands and their associated data
/// from a specific format (e.g., Orleans binary, JSON, Protocol Buffers).
/// </remarks>
public interface ILogEntryReader : IDisposable
{
    /// <summary>
    /// Reads a command identifier.
    /// </summary>
    /// <returns>The command type value.</returns>
    uint ReadCommand();

    /// <summary>
    /// Reads an unsigned 32-bit integer value.
    /// </summary>
    /// <returns>The value that was read.</returns>
    uint ReadUInt32();

    /// <summary>
    /// Reads an unsigned 64-bit integer value.
    /// </summary>
    /// <returns>The value that was read.</returns>
    ulong ReadUInt64();

    /// <summary>
    /// Reads a single byte value.
    /// </summary>
    /// <returns>The byte that was read.</returns>
    byte ReadByte();

    /// <summary>
    /// Reads a value of type <typeparamref name="T"/> using the provided codec.
    /// </summary>
    /// <typeparam name="T">The type of value to read.</typeparam>
    /// <param name="codec">The codec to use for deserialization.</param>
    /// <returns>The deserialized value.</returns>
    T ReadValue<T>(ILogDataCodec<T> codec);
}

/// <summary>
/// Factory for creating log entry readers and writers for a specific serialization format.
/// </summary>
/// <remarks>
/// <para>
/// Each implementation of this interface represents a specific serialization format
/// (e.g., Orleans binary, JSON, Protocol Buffers). The <see cref="Version"/> property
/// identifies the format and is written as the first byte of each log entry to enable
/// format discrimination during recovery.
/// </para>
/// <example>
/// <code>
/// // Use JSON format for log entries
/// builder.AddStateMachineStorage().UseJsonCodec();
///
/// // Use Protocol Buffers format for log entries
/// builder.AddStateMachineStorage().UseProtobufCodec();
/// </code>
/// </example>
/// </remarks>
public interface ILogEntryCodecFactory
{
    /// <summary>
    /// Gets the version byte that identifies this serialization format.
    /// </summary>
    /// <remarks>
    /// This value is written as the first byte of each log entry. During recovery,
    /// it is used to select the appropriate codec for deserialization.
    /// Well-known values: 0 = Orleans binary (legacy), 1 = JSON, 2 = Protocol Buffers.
    /// </remarks>
    byte Version { get; }

    /// <summary>
    /// Creates a new log entry writer for this format.
    /// </summary>
    /// <returns>A new <see cref="ILogEntryWriter"/> instance.</returns>
    ILogEntryWriter CreateWriter();

    /// <summary>
    /// Creates a new log entry reader for the provided data.
    /// </summary>
    /// <param name="data">The serialized log entry data (excluding the version byte).</param>
    /// <returns>A new <see cref="ILogEntryReader"/> instance.</returns>
    ILogEntryReader CreateReader(ReadOnlySequence<byte> data);
}
