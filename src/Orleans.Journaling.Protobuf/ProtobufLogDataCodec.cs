using System.Buffers;
using Google.Protobuf;

namespace Orleans.Journaling.Protobuf;

/// <summary>
/// An <see cref="ILogDataCodec{T}"/> implementation that uses Google Protocol Buffers for serialization.
/// </summary>
/// <typeparam name="T">The type of value to serialize and deserialize. Must implement <see cref="IMessage{T}"/>.</typeparam>
/// <remarks>
/// <para>
/// This codec requires that <typeparamref name="T"/> is a protobuf-generated message type
/// (i.e., it implements <see cref="IMessage{T}"/>). This is an intentional constraint of using
/// Google.Protobuf directly rather than protobuf-net.
/// </para>
/// <example>
/// <code>
/// // Use Protocol Buffers for all durable state machine serialization
/// builder.AddStateMachineStorage().UseProtobufCodec();
/// </code>
/// </example>
/// </remarks>
public sealed class ProtobufLogDataCodec<T> : ILogDataCodec<T> where T : IMessage<T>, new()
{
    private static readonly MessageParser<T> Parser = new(() => new T());

    /// <inheritdoc/>
    public void Write(T value, IBufferWriter<byte> output)
    {
        value.WriteTo(output);
    }

    /// <inheritdoc/>
    public T Read(ReadOnlySequence<byte> input, out long bytesConsumed)
    {
        var result = Parser.ParseFrom(input);
        bytesConsumed = input.Length;
        return result;
    }
}
