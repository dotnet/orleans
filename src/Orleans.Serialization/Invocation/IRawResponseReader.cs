using Orleans.Serialization.Buffers;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization.Invocation;

/// <summary>
/// Reads raw invocation responses for a statically registered result type.
/// </summary>
public interface IRawResponseReader
{
    /// <summary>
    /// Gets whether this reader preserves the selected codec's raw response contract.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// Reads a raw response using the supplied message session.
    /// </summary>
    /// <typeparam name="TInput">The reader input type.</typeparam>
    /// <param name="reader">The message body reader.</param>
    /// <param name="field">The result-type field header.</param>
    /// <returns>The reconstructed response.</returns>
    Response ReadRaw<TInput>(ref Reader<TInput> reader, scoped ref Field field);
}
