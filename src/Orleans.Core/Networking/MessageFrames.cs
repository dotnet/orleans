using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Runtime.Messaging;

/// <summary>
/// Writes messages in the framing which connections use, and reads them back with their bodies left serialized, as received messages hold them.
/// </summary>
/// <remarks>
/// A message read back from a frame is equivalent to one received from a connection: its headers are decoded, and its body is decoded when it is first accessed.
/// Forwarding such a message sends its serialized body without decoding it.
/// </remarks>
internal static class MessageFrames
{
    /// <summary>
    /// Appends a frame for <paramref name="message"/> to <paramref name="buffer"/> using <paramref name="serializer"/>.
    /// </summary>
    /// <returns>The lengths of the message's serialized headers and body.</returns>
    /// <remarks>If writing fails, <paramref name="buffer"/> is left unchanged.</remarks>
    public static (int HeaderLength, int BodyLength) Write(MessageSerializer serializer, ArcBufferWriter buffer, Message message)
    {
        var startLength = buffer.Length;
        try
        {
            var framing = buffer.GetSpan(Message.LENGTH_HEADER_SIZE);
            buffer.AdvanceWriter(Message.LENGTH_HEADER_SIZE);
            var (headerLength, bodyLength) = serializer.Write(buffer, message);
            BinaryPrimitives.WriteInt32LittleEndian(framing, headerLength);
            BinaryPrimitives.WriteInt32LittleEndian(framing[sizeof(int)..], bodyLength);
            return (headerLength, bodyLength);
        }
        catch
        {
            buffer.Truncate(startLength);
            throw;
        }
    }

    /// <summary>
    /// Reads the single frame which <paramref name="buffer"/> holds, leaving the message's body serialized.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The read request is owned by the message, or reset, as when a connection receives the message.")]
    public static Message Read(MessageHandlerShared shared, ArcBufferWriter buffer)
    {
        var serializer = shared.GetMessageSerializer();
        try
        {
            Span<byte> lengths = stackalloc byte[Message.LENGTH_HEADER_SIZE];
            if (buffer.Peek(lengths) != lengths.Length)
            {
                throw new InvalidMessageFrameException("The frame is incomplete.");
            }

            var headerLength = BinaryPrimitives.ReadInt32LittleEndian(lengths);
            var bodyLength = BinaryPrimitives.ReadInt32LittleEndian(lengths[sizeof(int)..]);
            serializer.ValidateFrameLengths(headerLength, bodyLength);
            if (buffer.Length != Message.LENGTH_HEADER_SIZE + headerLength + bodyLength)
            {
                throw new InvalidMessageFrameException("The frame's length does not match its contents.");
            }

            buffer.AdvanceReader(Message.LENGTH_HEADER_SIZE);
            var readRequest = shared.GetReceiveMessageHandler();
            readRequest.Initialize(buffer.ConsumeSlice(headerLength), buffer.ConsumeSlice(bodyLength));
            Message message;
            try
            {
                serializer.ReadHeaders(readRequest, out message);
            }
            catch
            {
                readRequest.Reset();
                throw;
            }

            if (bodyLength > 0)
            {
                message.SetMessageReadRequest(readRequest);
            }
            else
            {
                readRequest.Reset();
            }

            return message;
        }
        finally
        {
            shared.Return(serializer);
        }
    }
}

/// <summary>
/// Serializes a message as its frame, so that a message can be carried within another message.
/// </summary>
/// <remarks>
/// Serializing a message consumes it as sending it does: values such as <see cref="PooledBuffer"/> arguments release their storage.
/// A deserialized message holds its serialized body, as a received message does.
/// </remarks>
[RegisterSerializer]
internal sealed class MessageCodec(IServiceProvider serviceProvider) : IFieldCodec<Message>
{
    private MessageHandlerShared? _shared;

    // Resolved on first use, since the messaging services depend on the serializer.
    private MessageHandlerShared Shared => _shared ??= serviceProvider.GetRequiredService<MessageHandlerShared>();

    public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type? expectedType, [System.Diagnostics.CodeAnalysis.AllowNull] Message value)
        where TBufferWriter : IBufferWriter<byte>
    {
        Debug.Assert(value is not null);
        ReferenceCodec.MarkValueField(writer.Session);
        writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(Message), WireType.LengthPrefixed);

        using var frame = new ArcBufferWriter();
        var serializer = Shared.GetMessageSerializer();
        try
        {
            MessageFrames.Write(serializer, frame, value);
        }
        finally
        {
            Shared.Return(serializer);
        }

        writer.WriteVarUInt32((uint)frame.Length);
        using var bytes = frame.PeekSlice(frame.Length);
        foreach (var span in bytes)
        {
            writer.Write(span);
        }
    }

    public Message ReadValue<TInput>(ref Reader<TInput> reader, Field field)
    {
        ReferenceCodec.MarkValueField(reader.Session);
        field.EnsureWireType(WireType.LengthPrefixed);
        var length = (int)reader.ReadVarUInt32();
        using var frame = new ArcBufferWriter();
        var output = frame;
        reader.ReadBytes(ref output, length);
        return MessageFrames.Read(Shared, frame);
    }
}
