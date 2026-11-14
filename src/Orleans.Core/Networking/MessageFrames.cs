using System;
using System.Buffers.Binary;
using Orleans.Serialization.Buffers;

namespace Orleans.Runtime.Messaging;

/// <summary>
/// Reads and writes messages in connection framing, leaving decoded message bodies serialized.
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
    public static Message Read(MessageHandlerShared shared, ArcBufferWriter buffer)
    {
        using var frame = buffer.ConsumeSlice(buffer.Length);
        return Read(shared, frame);
    }

    /// <summary>
    /// Reads a frame, retaining independent slices for the message while the caller keeps ownership of <paramref name="buffer"/>.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The read request is owned by the message, or reset, as when a connection receives the message.")]
    public static Message Read(MessageHandlerShared shared, in ArcBuffer buffer)
    {
        var serializer = shared.GetMessageSerializer();
        try
        {
            Span<byte> lengths = stackalloc byte[Message.LENGTH_HEADER_SIZE];
            if (buffer.Length < lengths.Length)
            {
                throw new InvalidMessageFrameException("The frame is incomplete.");
            }

            using (var framing = buffer.Slice(0, lengths.Length))
            {
                framing.CopyTo(lengths);
            }

            var headerLength = BinaryPrimitives.ReadInt32LittleEndian(lengths);
            var bodyLength = BinaryPrimitives.ReadInt32LittleEndian(lengths[sizeof(int)..]);
            serializer.ValidateFrameLengths(headerLength, bodyLength);
            if (buffer.Length != Message.LENGTH_HEADER_SIZE + headerLength + bodyLength)
            {
                throw new InvalidMessageFrameException("The frame's length does not match its contents.");
            }

            var readRequest = shared.GetReceiveMessageHandler();
            readRequest.Initialize(
                buffer.Slice(Message.LENGTH_HEADER_SIZE, headerLength),
                buffer.Slice(Message.LENGTH_HEADER_SIZE + headerLength, bodyLength));
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
