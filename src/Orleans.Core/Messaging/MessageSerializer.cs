#nullable enable

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Networking.Shared;
using static Orleans.Runtime.Message;
using Orleans.Serialization;
using Orleans.Serialization.Session;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Buffers;
using System.Diagnostics;

namespace Orleans.Runtime.Messaging
{
    internal class MessageSerializer : IMessageSerializer
    {
        private const int FramingLength = Message.LENGTH_HEADER_SIZE;
        private const int MessageSizeHint = 4096;
        private readonly Serializer<object> _bodySerializer;
        private readonly Serializer<GrainAddress> _activationAddressCodec;
        private readonly CachingSiloAddressCodec _readerSiloAddressCodec;
        private readonly CachingSiloAddressCodec _writerSiloAddressCodec;
        private readonly CachingIdSpanCodec _readerIdSpanCodec;
        private readonly CachingIdSpanCodec _writerIdSpanCodec;
        private readonly Serializer _serializer;
        private readonly SerializerSession _serializationSession;
        private readonly SerializerSession _deserializationSession;
        private readonly MemoryPool<byte> _memoryPool;
        private readonly int _maxHeaderLength;
        private readonly int _maxBodyLength;
        private readonly SerializerSessionPool _sessionPool;
        private readonly DictionaryCodec<string, object> _requestContextCodec;
        private object? _bufferWriter;

        public MessageSerializer(
            Serializer<object> bodySerializer,
            SerializerSessionPool sessionPool,
            SharedMemoryPool memoryPool,
            IServiceProvider services,
            Serializer<GrainAddress> activationAddressSerializer,
            ICodecProvider codecProvider,
            int maxHeaderSize,
            int maxBodySize)
        {
            _readerSiloAddressCodec = new CachingSiloAddressCodec();
            _writerSiloAddressCodec = new CachingSiloAddressCodec();
            _readerIdSpanCodec = new CachingIdSpanCodec();
            _writerIdSpanCodec = new CachingIdSpanCodec();
            _serializer = ActivatorUtilities.CreateInstance<Serializer>(services);
            _activationAddressCodec = activationAddressSerializer;
            _serializationSession = sessionPool.GetSession();
            _deserializationSession = sessionPool.GetSession();
            _memoryPool = memoryPool.Pool;
            _bodySerializer = bodySerializer;
            _maxHeaderLength = maxHeaderSize;
            _maxBodyLength = maxBodySize;
            _sessionPool = sessionPool;
            _requestContextCodec = OrleansGeneratedCodeHelper.GetService<DictionaryCodec<string, object>>(this, codecProvider);
        }

        public (int RequiredBytes, int HeaderLength, int BodyLength) TryRead(ref ReadOnlySequence<byte> input, out Message? message)
        {
            if (input.Length < FramingLength)
            {
                message = default;
                return (FramingLength, 0, 0);
            }

            Span<byte> lengthBytes = stackalloc byte[FramingLength];
            input.Slice(input.Start, FramingLength).CopyTo(lengthBytes);
            var headerLength = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
            var bodyLength = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes.Slice(4));

            // Check lengths
            ThrowIfLengthsInvalid(headerLength, bodyLength);

            var requiredBytes = FramingLength + headerLength + bodyLength;
            if (input.Length < requiredBytes)
            {
                message = default;
                return (requiredBytes, 0, 0);
            }

            try
            {
                // Decode header
                var header = input.Slice(FramingLength, headerLength);

                // Decode body
                int bodyOffset = FramingLength + headerLength;
                var body = input.Slice(bodyOffset, bodyLength);

                // Build message
                message = new();
                if (header.IsSingleSegment)
                {
                    var headersReader = Reader.Create(header.First.Span, _deserializationSession);
                    Deserialize(ref headersReader, message);
                }
                else
                {
                    var headersReader = Reader.Create(header, _deserializationSession);
                    Deserialize(ref headersReader, message);
                }

                _deserializationSession.PartialReset();

                // Body deserialization is more likely to fail than header deserialization.
                // Separating the two allows for these kinds of errors to be propagated back to the caller.
                if (body.IsSingleSegment)
                {
                    message.BodyObject = _bodySerializer.Deserialize(body.First.Span, _deserializationSession);
                }
                else
                {
                    message.BodyObject = _bodySerializer.Deserialize(body, _deserializationSession);
                }

                return (0, headerLength, bodyLength);
            }
            finally
            {
                input = input.Slice(requiredBytes);
                _deserializationSession.PartialReset();
            }
        }

        public (int HeaderLength, int BodyLength) Write<TBufferWriter>(ref TBufferWriter writer, Message message) where TBufferWriter : IBufferWriter<byte>
        {
            try
            {
                if (_bufferWriter is not PrefixingBufferWriter<byte, TBufferWriter> bufferWriter)
                {
                    _bufferWriter = bufferWriter = new PrefixingBufferWriter<byte, TBufferWriter>(FramingLength, MessageSizeHint, _memoryPool);
                }

                bufferWriter.Reset(writer);
                var buffer = new MessageBufferWriter<TBufferWriter>(bufferWriter);
                Span<byte> lengthFields = stackalloc byte[FramingLength];

                var headerWriter = Writer.Create(buffer, _serializationSession);
                Serialize(ref headerWriter, message);
                headerWriter.Commit();

                var headerLength = bufferWriter.CommittedBytes;

                _serializationSession.PartialReset();
                _bodySerializer.Serialize(message.BodyObject, buffer, _serializationSession);

                // Write length prefixes, first header length then body length.
                BinaryPrimitives.WriteInt32LittleEndian(lengthFields, headerLength);

                var bodyLength = bufferWriter.CommittedBytes - headerLength;
                BinaryPrimitives.WriteInt32LittleEndian(lengthFields.Slice(4), bodyLength);

                // Before completing, check lengths
                ThrowIfLengthsInvalid(headerLength, bodyLength);

                bufferWriter.Complete(lengthFields);
                return (headerLength, bodyLength);
            }
            finally
            {
                _serializationSession.PartialReset();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ThrowIfLengthsInvalid(int headerLength, int bodyLength)
        {
            if (headerLength <= 0 || headerLength > _maxHeaderLength)
            {
                throw new OrleansException($"Invalid header size: {headerLength} (max configured value is {_maxHeaderLength}, see {nameof(MessagingOptions.MaxMessageHeaderSize)})");
            }

            if (bodyLength < 0 || bodyLength > _maxBodyLength)
            {
                throw new OrleansException($"Invalid body size: {bodyLength} (max configured value is {_maxBodyLength}, see {nameof(MessagingOptions.MaxMessageBodySize)})");
            }
        }

        private Message Serialize<TBufferWriter>(ref Writer<TBufferWriter> writer, Message value) where TBufferWriter : IBufferWriter<byte>
        {
            var headers = value.Headers;
            writer.WriteUInt32((uint)headers);

            writer.WriteInt64(value.Id.ToInt64());
            WriteGrainId(ref writer, value.SendingGrain);
            WriteGrainId(ref writer, value.TargetGrain);
            _writerSiloAddressCodec.WriteRaw(ref writer, value.SendingSilo);
            _writerSiloAddressCodec.WriteRaw(ref writer, value.TargetSilo);

            if (headers.HasFlag(MessageFlags.HasTimeToLive))
            {
                writer.WriteInt32((int)value.GetTimeToLiveMilliseconds());
            }

            if (headers.HasFlag(MessageFlags.HasInterfaceType))
            {
                _writerIdSpanCodec.WriteRaw(ref writer, value.InterfaceType.Value);
            }

            if (headers.HasFlag(MessageFlags.HasInterfaceVersion))
            {
                writer.WriteVarUInt32(value.InterfaceVersion);
            }

            if (headers.HasFlag(MessageFlags.HasCallChainId))
            {
                GuidCodec.WriteRaw(ref writer, value.CallChainId);
            }

            if (headers.HasFlag(MessageFlags.HasCacheInvalidationHeader))
            {
                WriteCacheInvalidationHeaders(ref writer, value.CacheInvalidationHeader);
            }

            // Always write RequestContext last
            if (headers.HasFlag(MessageFlags.HasRequestContextData))
            {
                WriteRequestContext(ref writer, value.RequestContextData);
            }

            return value;
        }

        private void Deserialize<TInput>(ref Reader<TInput> reader, Message result)
        {
            var headers = (PackedHeaders)reader.ReadUInt32();

            result.Headers = headers;
            result.Id = new CorrelationId(reader.ReadInt64());
            result.SendingGrain = ReadGrainId(ref reader);
            result.TargetGrain = ReadGrainId(ref reader);
            result.SendingSilo = _readerSiloAddressCodec.ReadRaw(ref reader);
            result.TargetSilo = _readerSiloAddressCodec.ReadRaw(ref reader);

            if (headers.HasFlag(MessageFlags.HasTimeToLive))
            {
                result.SetTimeToLiveMilliseconds(reader.ReadInt32());
            }
            else
            {
                result.SetInfiniteTimeToLive();
            }

            if (headers.HasFlag(MessageFlags.HasInterfaceType))
            {
                var interfaceTypeSpan = _readerIdSpanCodec.ReadRaw(ref reader);
                result.InterfaceType = new GrainInterfaceType(interfaceTypeSpan);
            }

            if (headers.HasFlag(MessageFlags.HasInterfaceVersion))
            {
                result.InterfaceVersion = (ushort)reader.ReadVarUInt32();
            }

            if (headers.HasFlag(MessageFlags.HasCallChainId))
            {
                result.CallChainId = GuidCodec.ReadRaw(ref reader);
            }

            if (headers.HasFlag(MessageFlags.HasCacheInvalidationHeader))
            {
                result.CacheInvalidationHeader = ReadCacheInvalidationHeaders(ref reader);
            }

            if (headers.HasFlag(MessageFlags.HasRequestContextData))
            {
                result.RequestContextData = ReadRequestContext(ref reader);
            }
        }

        private List<GrainAddress> ReadCacheInvalidationHeaders<TInput>(ref Reader<TInput> reader)
        {
            var n = reader.ReadVarUInt32();
            if (n > 0)
            {
                var list = new List<GrainAddress>((int)n);
                for (int i = 0; i < n; i++)
                {
                    list.Add(_activationAddressCodec.Deserialize(ref reader));
                }

                return list;
            }

            return new List<GrainAddress>();
        }

        private void WriteCacheInvalidationHeaders<TBufferWriter>(ref Writer<TBufferWriter> writer, List<GrainAddress> value) where TBufferWriter : IBufferWriter<byte>
        {
            writer.WriteVarUInt32((uint)value.Count);
            foreach (var entry in value)
            {
                _activationAddressCodec.Serialize(entry, ref writer);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static string? ReadString<TInput>(ref Reader<TInput> reader)
        {
            var length = reader.ReadVarInt32();
            if (length <= 0)
            {
                if (length < 0)
                {
                    return null;
                }

                return string.Empty;
            }

            string result;
#if NETCOREAPP3_1_OR_GREATER
            if (reader.TryReadBytes(length, out var span))
            {
                result = Encoding.UTF8.GetString(span);
            }
            else
#endif
            {
                var bytes = reader.ReadBytes((uint)length);
                result = Encoding.UTF8.GetString(bytes);
            }

            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteString<TBufferWriter>(ref Writer<TBufferWriter> writer, string value) where TBufferWriter : IBufferWriter<byte>
        {
            if (value is null)
            {
                writer.WriteVarInt32(-1);
                return;
            }

#if NETCOREAPP3_1_OR_GREATER
            var numBytes = Encoding.UTF8.GetByteCount(value);
            writer.WriteVarInt32(numBytes);
            if (numBytes < 512)
            {
                writer.EnsureContiguous(numBytes);
            }

            var currentSpan = writer.WritableSpan;

            // If there is enough room in the current span for the encoded data,
            // then encode directly into the output buffer.
            if (numBytes <= currentSpan.Length)
            {
                Encoding.UTF8.GetBytes(value, currentSpan);
                writer.AdvanceSpan(numBytes);
            }
            else
            {
                // Note: there is room for optimization here.
                Span<byte> bytes = Encoding.UTF8.GetBytes(value);
                writer.Write(bytes);
            }
#else
            var bytes = Encoding.UTF8.GetBytes(value);
            writer.WriteVarInt32(bytes.Length);
            writer.Write(bytes);
#endif
        }

        public void WriteRequestContext<TBufferWriter>(ref Writer<TBufferWriter> writer, Dictionary<string, object> value) where TBufferWriter : IBufferWriter<byte>
        {
            writer.WriteVarUInt32((uint)value.Count);
            foreach (var entry in value)
            {
                WriteString(ref writer, entry.Key);
                _serializer.Serialize(entry.Value, ref writer);
            }
        }

        public Dictionary<string, object> ReadRequestContext<TInput>(ref Reader<TInput> reader)
        {
            var size = (int)reader.ReadVarUInt32();
            var result = new Dictionary<string, object>(size);
            for (var i = 0; i < size; i++)
            {
                var key = ReadString(ref reader);
                var value = _serializer.Deserialize<object, TInput>(ref reader);

                Debug.Assert(key is not null);
                result.Add(key, value);
            }

            return result;
        }

        private GrainId ReadGrainId<TInput>(ref Reader<TInput> reader)
        {
            var grainType = _readerIdSpanCodec.ReadRaw(ref reader);
            var grainKey = IdSpanCodec.ReadRaw(ref reader);
            return new GrainId(new GrainType(grainType), grainKey);
        }

        private void WriteGrainId<TBufferWriter>(ref Writer<TBufferWriter> writer, GrainId value) where TBufferWriter : IBufferWriter<byte>
        {
            _writerIdSpanCodec.WriteRaw(ref writer, value.Type.Value);
            IdSpanCodec.WriteRaw(ref writer, value.Key);
        }

        private static ActivationId ReadActivationId<TInput>(ref Reader<TInput> reader)
        {
            if (reader.ReadByte() == 0)
            {
                return default;
            }

            if (reader.TryReadBytes(16, out var readOnly))
            {
                return new(new Guid(readOnly));
            }

            Span<byte> bytes = stackalloc byte[16];
            for (var i = 0; i < 16; i++)
            {
                bytes[i] = reader.ReadByte();
            }

            return new(new Guid(bytes));
        }

        private static void WriteActivationId<TBufferWriter>(ref Writer<TBufferWriter> writer, ActivationId value) where TBufferWriter : IBufferWriter<byte>
        {
            if (value.IsDefault)
            {
                writer.WriteByte(0);
                return;
            }

            writer.WriteByte(1);
            writer.EnsureContiguous(16);
            if (value.Key.TryWriteBytes(writer.WritableSpan))
            {
                writer.AdvanceSpan(16);
                return;
            }

            writer.Write(value.Key.ToByteArray());
        }
    }
}
