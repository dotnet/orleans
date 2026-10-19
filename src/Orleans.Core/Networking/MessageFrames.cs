using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Orleans.Serialization.Buffers;

namespace Orleans.Runtime.Messaging;

/// <summary>
/// Writes messages in the framing which connections use, and reads them back with their bodies left serialized, as received messages hold them.
/// </summary>
/// <remarks>
/// A message read back from frames is equivalent to one received from a connection: its headers are decoded, and its body is decoded when it is first accessed.
/// Forwarding such a message sends its serialized body without decoding it.
/// </remarks>
internal static class MessageFrames
{
    /// <summary>
    /// Appends a frame for <paramref name="message"/> to <paramref name="buffer"/>.
    /// </summary>
    /// <returns>The number of bytes appended.</returns>
    /// <remarks>
    /// Serializing a message can release the storage of some of its values, such as <see cref="PooledBuffer"/> arguments,
    /// so a message which has been written is delivered by reading it back rather than by serializing it again.
    /// If writing fails, <paramref name="buffer"/> is left unchanged.
    /// </remarks>
    public static int Write(MessageHandlerShared shared, ArcBufferWriter buffer, Message message)
    {
        var serializer = shared.GetMessageSerializer();
        try
        {
            var (headerLength, bodyLength) = Write(serializer, buffer, message);
            return Message.LENGTH_HEADER_SIZE + headerLength + bodyLength;
        }
        finally
        {
            shared.Return(serializer);
        }
    }

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
    /// Reads the messages in <paramref name="frames"/>, leaving their bodies serialized.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Each read request is owned by its message, or reset, as when a connection receives the message.")]
    public static List<Message> Read(MessageHandlerShared shared, ReadOnlySpan<byte> frames)
    {
        using var buffer = new ArcBufferWriter();
        buffer.Write(frames);
        var messages = new List<Message>();
        var serializer = shared.GetMessageSerializer();
        try
        {
            Span<byte> lengths = stackalloc byte[Message.LENGTH_HEADER_SIZE];
            while (buffer.Length > 0)
            {
                if (buffer.Peek(lengths) != lengths.Length)
                {
                    throw new InvalidMessageFrameException("The frames end with an incomplete frame.");
                }

                var headerLength = BinaryPrimitives.ReadInt32LittleEndian(lengths);
                var bodyLength = BinaryPrimitives.ReadInt32LittleEndian(lengths[sizeof(int)..]);
                serializer.ValidateFrameLengths(headerLength, bodyLength);
                if (buffer.Length < Message.LENGTH_HEADER_SIZE + headerLength + bodyLength)
                {
                    throw new InvalidMessageFrameException("The frames end with an incomplete frame.");
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

                messages.Add(message);
            }
        }
        catch
        {
            foreach (var message in messages)
            {
                message.Dispose();
            }

            throw;
        }
        finally
        {
            shared.Return(serializer);
        }

        return messages;
    }
}
