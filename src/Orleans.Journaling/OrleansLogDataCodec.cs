using System.Buffers;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Session;

namespace Orleans.Journaling;

/// <summary>
/// An <see cref="ILogDataCodec{T}"/> implementation that wraps an Orleans <see cref="IFieldCodec{T}"/>
/// and <see cref="SerializerSessionPool"/> to produce the same wire format as the legacy Orleans binary encoding.
/// </summary>
/// <typeparam name="T">The type of value to serialize and deserialize.</typeparam>
/// <remarks>
/// This adapter bridges the new <see cref="ILogDataCodec{T}"/> abstraction to the existing Orleans
/// serialization infrastructure, preserving backward compatibility with data written using the
/// legacy Orleans binary format.
/// </remarks>
internal sealed class OrleansLogDataCodec<T>(IFieldCodec<T> fieldCodec, SerializerSessionPool sessionPool) : ILogDataCodec<T>
{
    /// <inheritdoc/>
    public void Write(T value, IBufferWriter<byte> output)
    {
        using var session = sessionPool.GetSession();
        var writer = Writer.Create(output, session);
        fieldCodec.WriteField(ref writer, 0, typeof(T), value);
        writer.Commit();
    }

    /// <inheritdoc/>
    public T Read(ReadOnlySequence<byte> input, out long bytesConsumed)
    {
        using var session = sessionPool.GetSession();
        var reader = Reader.Create(input, session);
        var field = reader.ReadFieldHeader();
        var result = fieldCodec.ReadValue(ref reader, field);
        bytesConsumed = reader.Position;
        return result;
    }
}
