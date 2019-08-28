using System;
using System.Buffers;
using System.Buffers.Binary;
using Orleans.Configuration;
using Orleans.Networking.Shared;
using Orleans.Serialization;

namespace Orleans.Runtime.Messaging
{
    internal sealed class MessageSerializer : IMessageSerializer
    {
        private readonly HeadersSerializer headersSerializer;
        private readonly OrleansSerializer<object> objectSerializer;
        private readonly MemoryPool<byte> memoryPool;
        private readonly int maxHeaderLength;
        private readonly int maxBodyLength;

        public MessageSerializer(
            SerializationManager serializationManager,
            SharedMemoryPool memoryPool,
            int maxHeaderSize,
            int maxBodySize)
        {
            this.headersSerializer = new HeadersSerializer(serializationManager);
            this.objectSerializer = new OrleansSerializer<object>(serializationManager);
            this.memoryPool = memoryPool.Pool;
            this.maxHeaderLength = maxHeaderSize;
            this.maxBodyLength = maxBodySize;
        }

        public int TryRead(ref ReadOnlySequence<byte> input, out Message message)
        {
            message = default;
            if (input.Length < 8)
            {
                return 8;
            }

            (int, int) ReadLengths(ReadOnlySequence<byte> b)
            {
                Span<byte> lengthBytes = stackalloc byte[8];
                b.Slice(0, 8).CopyTo(lengthBytes);
                return (BinaryPrimitives.ReadInt32LittleEndian(lengthBytes), BinaryPrimitives.ReadInt32LittleEndian(lengthBytes.Slice(4)));
            }

            var (headerLength, bodyLength) = ReadLengths(input);

            // Check lengths
            ThrowIfLengthsInvalid(headerLength, bodyLength);

            var requiredBytes = 8 + headerLength + bodyLength;
            if (input.Length < requiredBytes)
            {
                message = default;
                return requiredBytes;
            }

            if (headerLength == 0)
            {
                input = input.Slice(requiredBytes);
                message = default;
                return requiredBytes;
            }

            // decode header
            var header = input.Slice(Message.LENGTH_HEADER_SIZE, headerLength);

            // decode body
            int bodyOffset = Message.LENGTH_HEADER_SIZE + headerLength;
            var body = input.Slice(bodyOffset, bodyLength);

            // build message
            try
            {
                this.headersSerializer.Deserialize(header, out var headersContainer);
                message = new Message
                {
                    Headers = headersContainer
                };

                // Body deserialization is more likely to fail than header deserialization.
                // Separating the two allows for these kinds of errors to be propagated back to the caller.
                this.objectSerializer.Deserialize(body, out var bodyObject);
                message.BodyObject = bodyObject;
            }
            finally
            {
                input = input.Slice(requiredBytes);
            }

            return 0;
        }

        public void Write<TBufferWriter>(ref TBufferWriter writer, Message message) where TBufferWriter : IBufferWriter<byte>
        {
            var buffer = new PrefixingBufferWriter<byte, TBufferWriter>(writer, 8, 4096, this.memoryPool);
            Span<byte> lengthFields = stackalloc byte[8];

            this.headersSerializer.Serialize(buffer, message.Headers);
            var headerLength = buffer.CommittedBytes;

            this.objectSerializer.Serialize(buffer, message.BodyObject);

            // Write length prefixes, first header length then body length.
            BinaryPrimitives.WriteInt32LittleEndian(lengthFields, headerLength);

            var bodyLength = buffer.CommittedBytes - headerLength;
            BinaryPrimitives.WriteInt32LittleEndian(lengthFields.Slice(4), bodyLength);

            // Before completing, check lengths
            ThrowIfLengthsInvalid(headerLength, bodyLength);

            buffer.Complete(lengthFields);
        }

        private void ThrowIfLengthsInvalid(int headerLength, int bodyLength)
        {
            if (headerLength <= 0 || headerLength > this.maxHeaderLength)
                throw new OrleansException($"Invalid header size: {headerLength} (max configured value is {this.maxHeaderLength}, see {nameof(MessagingOptions.MaxMessageHeaderSize)})");
            if (bodyLength < 0 || bodyLength > this.maxBodyLength)
                throw new OrleansException($"Invalid body size: {bodyLength} (max configured value is {this.maxBodyLength}, see {nameof(MessagingOptions.MaxMessageBodySize)})");
        }

        private sealed class OrleansSerializer<T>
        {
            private readonly SerializationManager serializationManager;
            private readonly BinaryTokenStreamReader2 reader = new BinaryTokenStreamReader2();
            private readonly SerializationContext serializationContext;
            private readonly DeserializationContext deserializationContext;

            public OrleansSerializer(SerializationManager serializationManager)
            {
                this.serializationManager = serializationManager;
                this.serializationContext = new SerializationContext(serializationManager);
                this.deserializationContext = new DeserializationContext(serializationManager)
                {
                    StreamReader = this.reader
                };
            }

            public void Deserialize(ReadOnlySequence<byte> input, out T value)
            {
                reader.PartialReset(input);
                try
                {
                    value = (T)SerializationManager.DeserializeInner(this.serializationManager, typeof(T), this.deserializationContext, this.reader);
                }
                finally
                {
                    this.deserializationContext.Reset();
                }
            }

            public void Serialize<TBufferWriter>(TBufferWriter output, T value) where TBufferWriter : IBufferWriter<byte>
            {
                var streamWriter = this.serializationContext.StreamWriter;
                if (streamWriter is BinaryTokenStreamWriter2<TBufferWriter> writer)
                {
                    writer.PartialReset(output);
                }
                else
                {
                    this.serializationContext.StreamWriter = writer = new BinaryTokenStreamWriter2<TBufferWriter>(output);
                }

                try
                {
                    SerializationManager.SerializeInner(this.serializationManager, value, typeof(T), this.serializationContext, writer);
                }
                finally
                {
                    writer.Commit();
                    this.serializationContext.Reset();
                }
            }
        }

        private sealed class HeadersSerializer
        {
            private readonly BinaryTokenStreamReader2 reader = new BinaryTokenStreamReader2();
            private readonly SerializationContext serializationContext;
            private readonly DeserializationContext deserializationContext;

            public HeadersSerializer(SerializationManager serializationManager)
            {
                this.serializationContext = new SerializationContext(serializationManager);
                this.deserializationContext = new DeserializationContext(serializationManager)
                {
                    StreamReader = this.reader
                };
            }

            public void Deserialize(ReadOnlySequence<byte> input, out Message.HeadersContainer value)
            {
                try
                {
                    reader.PartialReset(input);
                    value = (Message.HeadersContainer)Message.HeadersContainer.Deserializer(null, this.deserializationContext);
                }
                finally
                {
                    this.deserializationContext.Reset();
                }
            }

            public void Serialize<TBufferWriter>(TBufferWriter output, Message.HeadersContainer value) where TBufferWriter : IBufferWriter<byte>
            {
                var streamWriter = this.serializationContext.StreamWriter;
                if (streamWriter is BinaryTokenStreamWriter2<TBufferWriter> writer)
                {
                    writer.PartialReset(output);
                }
                else
                {
                    this.serializationContext.StreamWriter = writer = new BinaryTokenStreamWriter2<TBufferWriter>(output);
                }

                try
                {
                    Message.HeadersContainer.Serializer(value, this.serializationContext, null);
                }
                finally
                {
                    writer.Commit();
                    this.serializationContext.Reset();
                }
            }
        }
    }
}
