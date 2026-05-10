using System.Buffers;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Buffers.Adaptors;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Session;

namespace Orleans.Journaling;

/// <summary>
/// An <see cref="IJournalValueCodec{T}"/> implementation that wraps an Orleans <see cref="IFieldCodec{T}"/>
/// and <see cref="SerializerSessionPool"/> to produce the same wire format as the legacy Orleans binary encoding.
/// </summary>
/// <typeparam name="T">The type of value to serialize and deserialize.</typeparam>
/// <remarks>
/// This adapter bridges the new <see cref="IJournalValueCodec{T}"/> abstraction to the existing Orleans
/// serialization infrastructure, preserving backward compatibility with data written using the
/// legacy Orleans binary format.
/// </remarks>
internal sealed class OrleansJournalValueCodec<T>(IFieldCodec<T> fieldCodec, SerializerSessionPool sessionPool) : IOrleansBinaryValueCodec<T>
{
    /// <inheritdoc/>
    public IFieldCodec<T> FieldCodec => fieldCodec;

    /// <inheritdoc/>
    public void Write(T value, IBufferWriter<byte> output)
    {
        using var session = sessionPool.GetSession();
        var writer = Writer.Create(output, session);
        fieldCodec.WriteField(ref writer, 0, typeof(T), value);
        writer.Commit();
    }

    /// <inheritdoc/>
    public T Read(ref Reader<ArcBufferReaderInput> reader)
    {
        var field = reader.ReadFieldHeader();
        return fieldCodec.ReadValue(ref reader, field);
    }
}

/// <summary>
/// Internal extension of <see cref="IJournalValueCodec{T}"/> for the Orleans binary journal path that
/// exposes the underlying Orleans <see cref="IFieldCodec{T}"/>. Required by codecs (notably
/// <see cref="OrleansBinaryDictionaryOperationCodec{TKey, TValue}"/>) that must write multiple fields
/// into a single shared <see cref="Writer{TBufferWriter}"/>/<see cref="SerializerSession"/> in order to
/// preserve byte-for-byte compatibility with the legacy inlined writers in
/// <c>DurableDictionary</c>/<c>DurableList</c>/etc.
/// </summary>
internal interface IOrleansBinaryValueCodec<T> : IJournalValueCodec<T>
{
    IFieldCodec<T> FieldCodec { get; }
}
