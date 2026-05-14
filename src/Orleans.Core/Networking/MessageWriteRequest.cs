#nullable enable
using System;
using Orleans.Serialization.Buffers;
using System.Buffers.Binary;
using Orleans.Connections.Transport;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;

namespace Orleans.Runtime.Messaging
{
    internal sealed class MessageWriteRequest : WriteRequest
    {
        private readonly MessageHandlerShared _shared;
        private readonly ArcBufferWriter _buffer = new();
        public MessageWriteRequest(MessageHandlerShared shared)
        {
            _shared = shared;
            Buffers = new(_buffer);
        }

        public List<Message> Messages { get; } = [];

        public void WriteMessage(Message message)
        {
            Messages.Add(message);

            // Reserve space for framing
            var framingBytes = _buffer.GetSpan(Message.LENGTH_HEADER_SIZE);
            _buffer.AdvanceWriter(Message.LENGTH_HEADER_SIZE);

            // Serialize the message in full
            var messageSerializer = _shared.GetMessageSerializer();
            var (headerLength, bodyLength) = messageSerializer.Write(_buffer, message);
            _shared.Return(messageSerializer);

            // Write the framing
            BinaryPrimitives.WriteInt32LittleEndian(framingBytes, headerLength);
            BinaryPrimitives.WriteInt32LittleEndian(framingBytes[sizeof(int)..], bodyLength);
        }

        public override void SetResult()
        {
            Reset();
        }

        public override void SetException(Exception error)
        {
            // TODO: Reject the messages
            _shared.ConnectionTrace.LogError(error, "Error sending messages {Messages}", Messages);
            Reset();
        }

        public void Reset()
        {
            Messages.Clear();
            _buffer.Reset();
            _shared.Return(this);
        }
    }
}
