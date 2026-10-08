using System.Buffers;
using Orleans.Serialization.Buffers;

namespace Orleans.Serialization.Invocation;

/// <summary>
/// Writes an invocation response directly using its bound serialization dependencies.
/// </summary>
public interface IRawResponseWriter
{
    /// <summary>
    /// Writes the existing raw response representation into the supplied message session.
    /// </summary>
    /// <typeparam name="TBufferWriter">The output buffer writer type.</typeparam>
    /// <param name="writer">The message body writer.</param>
    void WriteRaw<TBufferWriter>(ref Writer<TBufferWriter> writer) where TBufferWriter : IBufferWriter<byte>;
}
