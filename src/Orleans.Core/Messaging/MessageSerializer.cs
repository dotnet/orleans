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
        private object _bufferWriter;

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

        public (int RequiredBytes, int HeaderLength, int BodyLength) TryRead(ref ReadOnlySequence<byte> input, out Message message)
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
                    DeserializeFast(ref headersReader, message);
                }
                else
                {
                    var headersReader = Reader.Create(header, _deserializationSession);
                    DeserializeFast(ref headersReader, message);
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
                SerializeFast(ref headerWriter, message);
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

        private Message SerializeFast<TBufferWriter>(ref Writer<TBufferWriter> writer, Message value) where TBufferWriter : IBufferWriter<byte>
        {
            var headers = value.GetHeadersMask();
            writer.WriteVarUInt32((uint)headers);

            if ((headers & Headers.CACHE_INVALIDATION_HEADER) != Headers.NONE)
            {
                this.WriteCacheInvalidationHeaders(ref writer, value.CacheInvalidationHeader);
            }

            if ((headers & Headers.CATEGORY) != Headers.NONE)
            {
                writer.WriteByte((byte)value.Category);
            }

            if ((headers & Headers.DIRECTION) != Headers.NONE)
            {
                writer.WriteByte((byte)value.Direction);
            }

            if ((headers & Headers.TIME_TO_LIVE) != Headers.NONE)
            {
                writer.WriteInt64(value.TimeToLive.Value.Ticks);
            }

            if ((headers & Headers.FORWARD_COUNT) != Headers.NONE)
            {
                writer.WriteVarUInt32((uint)value.ForwardCount);
            }

            if ((headers & Headers.CORRELATION_ID) != Headers.NONE)
            {
                writer.WriteInt64(value.Id.ToInt64());
            }

            if ((headers & Headers.INTERFACE_VERSION) != Headers.NONE)
            {
                writer.WriteVarUInt32((uint)value.InterfaceVersion);
            }

            if ((headers & Headers.REJECTION_INFO) != Headers.NONE)
            {
                WriteString(ref writer, value.RejectionInfo);
            }

            if ((headers & Headers.REJECTION_TYPE) != Headers.NONE)
            {
                writer.WriteByte((byte)value.RejectionType);
            }

            if ((headers & Headers.RESULT) != Headers.NONE)
            {
                writer.WriteByte((byte)value.Result);
            }

            if ((headers & Headers.SENDING_ACTIVATION) != Headers.NONE)
            {
                WriteActivationId(ref writer, value.SendingActivation);
            }

            if ((headers & Headers.SENDING_GRAIN) != Headers.NONE)
            {
                WriteGrainId(ref writer, value.SendingGrain);
            }

            if ((headers & Headers.SENDING_SILO) != Headers.NONE)
            {
                _writerSiloAddressCodec.WriteRaw(ref writer, value.SendingSilo);
            }

            if ((headers & Headers.TARGET_ACTIVATION) != Headers.NONE)
            {
                WriteActivationId(ref writer, value.TargetActivation);
            }

            if ((headers & Headers.TARGET_GRAIN) != Headers.NONE)
            {
                WriteGrainId(ref writer, value.TargetGrain);
            }

            if ((headers & Headers.CALL_CHAIN_ID) != Headers.NONE)
            {
                GuidCodec.WriteRaw(ref writer, value.CallChainId);
            }

            if ((headers & Headers.TARGET_SILO) != Headers.NONE)
            {
                _writerSiloAddressCodec.WriteRaw(ref writer, value.TargetSilo);
            }

            if ((headers & Headers.INTERFACE_TYPE) != Headers.NONE)
            {
                _writerIdSpanCodec.WriteRaw(ref writer, value.InterfaceType.Value);
            }

            // Always write RequestContext last
            if ((headers & Headers.REQUEST_CONTEXT) != Headers.NONE)
            {
                WriteRequestContext(ref writer, value.RequestContextData);
            }

            return value;
        }

        private void DeserializeFast<TInput>(ref Reader<TInput> reader, Message result)
        {
            var headers = (Headers)reader.ReadVarUInt32();

            if ((headers & Headers.CACHE_INVALIDATION_HEADER) != Headers.NONE)
            {
                result.CacheInvalidationHeader = this.ReadCacheInvalidationHeaders(ref reader);
            }

            if ((headers & Headers.CATEGORY) != Headers.NONE)
                result.Category = (Categories)reader.ReadByte();

            if ((headers & Headers.DIRECTION) != Headers.NONE)
                result.Direction = (Directions)reader.ReadByte();

            if ((headers & Headers.TIME_TO_LIVE) != Headers.NONE)
                result.TimeToLive = TimeSpan.FromTicks(reader.ReadInt64());

            if ((headers & Headers.FORWARD_COUNT) != Headers.NONE)
                result.ForwardCount = (int)reader.ReadVarUInt32();

            if ((headers & Headers.CORRELATION_ID) != Headers.NONE)
                result.Id = new CorrelationId(reader.ReadInt64());

            if ((headers & Headers.ALWAYS_INTERLEAVE) != Headers.NONE)
                result.IsAlwaysInterleave = true;

            if ((headers & Headers.INTERFACE_VERSION) != Headers.NONE)
                result.InterfaceVersion = (ushort)reader.ReadVarUInt32();

            if ((headers & Headers.READ_ONLY) != Headers.NONE)
                result.IsReadOnly = true;

            if ((headers & Headers.IS_UNORDERED) != Headers.NONE)
                result.IsUnordered = true;

            if ((headers & Headers.REJECTION_INFO) != Headers.NONE)
                result.RejectionInfo = ReadString(ref reader);

            if ((headers & Headers.REJECTION_TYPE) != Headers.NONE)
                result.RejectionType = (RejectionTypes)reader.ReadByte();

            if ((headers & Headers.RESULT) != Headers.NONE)
                result.Result = (ResponseTypes)reader.ReadByte();

            if ((headers & Headers.SENDING_ACTIVATION) != Headers.NONE)
            {
                result.SendingActivation = ReadActivationId(ref reader);
            }

            if ((headers & Headers.SENDING_GRAIN) != Headers.NONE)
            {
                result.SendingGrain = ReadGrainId(ref reader);
            }

            if ((headers & Headers.SENDING_SILO) != Headers.NONE)
            {
                result.SendingSilo = _readerSiloAddressCodec.ReadRaw(ref reader);
            }

            if ((headers & Headers.TARGET_ACTIVATION) != Headers.NONE)
            {
                result.TargetActivation = ReadActivationId(ref reader);
            }

            if ((headers & Headers.TARGET_GRAIN) != Headers.NONE)
            {
                result.TargetGrain = ReadGrainId(ref reader);
            }

            if ((headers & Headers.CALL_CHAIN_ID) != Headers.NONE)
            {
                result.CallChainId = GuidCodec.ReadRaw(ref reader);
            }

            if ((headers & Headers.TARGET_SILO) != Headers.NONE)
            {
                result.TargetSilo = _readerSiloAddressCodec.ReadRaw(ref reader);
            }

            if ((headers & Headers.INTERFACE_TYPE) != Headers.NONE)
            {
                var interfaceTypeSpan = _readerIdSpanCodec.ReadRaw(ref reader);
                result.InterfaceType = new GrainInterfaceType(interfaceTypeSpan);
            }

            if ((headers & Headers.REQUEST_CONTEXT) != Headers.NONE)
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
        private static string ReadString<TInput>(ref Reader<TInput> reader)
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
