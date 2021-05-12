using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Networking.Shared;
using static Orleans.Runtime.Message;
using static Orleans.Runtime.Message.HeadersContainer;
using Orleans.Serialization;
using Orleans.Serialization.Session;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Utilities;

namespace Orleans.Runtime.Messaging
{
    internal class MessageSerializer : IMessageSerializer
    {
        private const int FramingLength = Message.LENGTH_HEADER_SIZE;
        private const int MessageSizeHint = 4096;
        private readonly Serializer<object> _bodySerializer;
        private readonly Serializer<ActivationAddress> _activationAddressCodec;
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
            Serializer<ActivationAddress> activationAddressSerializer,
            ICodecProvider codecProvider,
            int maxHeaderSize,
            int maxBodySize)
        {
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
                // decode header
                var header = input.Slice(FramingLength, headerLength);

                // decode body
                int bodyOffset = FramingLength + headerLength;
                var body = input.Slice(bodyOffset, bodyLength);

                // build message
                HeadersContainer headersContainer;
                if (header.IsSingleSegment)
                {
                    var headersReader = Reader.Create(header.First.Span, _deserializationSession);
                    headersContainer = DeserializeFast(ref headersReader);
                }
                else
                {
                    var headersReader = Reader.Create(header, _deserializationSession);
                    headersContainer = DeserializeFast(ref headersReader);
                }

                _deserializationSession.PartialReset();
                message = new Message
                {
                    Headers = headersContainer
                };

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
                SerializeFast(ref headerWriter, message.Headers);
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

        private HeadersContainer SerializeFast<TBufferWriter>(ref Writer<TBufferWriter> writer, HeadersContainer value) where TBufferWriter : IBufferWriter<byte>
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

            if ((headers & Headers.REQUEST_CONTEXT) != Headers.NONE)
            {
                WriteRequestContext(ref writer, value.RequestContextData);
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
                WriteSiloAddress(ref writer, value.SendingSilo);
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
                writer.WriteInt64(value.CallChainId.ToInt64());
            }

            if ((headers & Headers.TARGET_SILO) != Headers.NONE)
            {
                WriteSiloAddress(ref writer, value.TargetSilo);
            }

            if ((headers & Headers.INTERFACE_TYPE) != Headers.NONE)
            {
                IdSpanCodec.WriteRaw(ref writer, value.InterfaceType.Value);
            }

            return value;
        }

        private HeadersContainer DeserializeFast<TInput>(ref Reader<TInput> reader)
        {
            var headers = (Headers)reader.ReadVarUInt32();
            var result = new HeadersContainer();

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

            if ((headers & Headers.IS_NEW_PLACEMENT) != Headers.NONE)
                result.IsNewPlacement = true;

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

            if ((headers & Headers.REQUEST_CONTEXT) != Headers.NONE)
            {
                result.RequestContextData = ReadRequestContext(ref reader);
            }

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
                result.SendingSilo = ReadSiloAddress(ref reader);
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
                result.CallChainId = new CorrelationId(reader.ReadInt64());
            }

            if ((headers & Headers.TARGET_SILO) != Headers.NONE)
            {
                result.TargetSilo = ReadSiloAddress(ref reader);
            }

            if ((headers & Headers.INTERFACE_TYPE) != Headers.NONE)
            {
                var interfaceTypeSpan = IdSpanCodec.ReadRaw(ref reader);
                result.InterfaceType = new GrainInterfaceType(interfaceTypeSpan);
            }

            return result;
        }

        private List<ActivationAddress> ReadCacheInvalidationHeaders<TInput>(ref Reader<TInput> reader)
        {
            var n = reader.ReadVarUInt32();
            if (n > 0)
            {
                var list = new List<ActivationAddress>((int)n);
                for (int i = 0; i < n; i++)
                {
                    list.Add(_activationAddressCodec.Deserialize(ref reader));
                }

                return list;
            }

            return new List<ActivationAddress>();
        }

        private void WriteCacheInvalidationHeaders<TBufferWriter>(ref Writer<TBufferWriter> writer, List<ActivationAddress> value) where TBufferWriter : IBufferWriter<byte>
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
#if NETCOREAPP
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

#if NETCOREAPP
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

        public static SiloAddress ReadSiloAddress<TInput>(ref Reader<TInput> reader)
        {
            IPAddress ip;
            var length = reader.ReadVarInt32();
            if (length < 0)
            {
                return null;
            }
#if NET5_0
            if (reader.TryReadBytes(length, out var bytes))
            {
                ip = new IPAddress(bytes);
            }
            else
            {
#endif
                var addressBytes = reader.ReadBytes((uint)length);
                ip = new IPAddress(addressBytes);
#if NET5_0
            }
#endif
            var port = (int)reader.ReadVarUInt32();
            var generation = reader.ReadInt32();
            
            return SiloAddress.New(new IPEndPoint(ip, port), generation);
        }

        public static void WriteSiloAddress<TBufferWriter>(ref Writer<TBufferWriter> writer, SiloAddress value) where TBufferWriter : IBufferWriter<byte>
        {
            if (value is null)
            {
                writer.WriteVarInt32(-1);
                return;
            }

            var ep = value.Endpoint;
#if NET5_0
            Span<byte> buffer = stackalloc byte[64];
            if (ep.Address.TryWriteBytes(buffer, out var length))
            {
                var writable = writer.WritableSpan;
                if (writable.Length > length)
                {
                    // IP
                    writer.WriteVarInt32(length);
                    buffer.Slice(0, length).CopyTo(writable[1..]);
                    writer.AdvanceSpan(length);

                    // Port
                    writer.WriteVarUInt32((uint)ep.Port);

                    // Generation
                    writer.WriteInt32(value.Generation);

                    return;
                }
            }
#endif

            // IP
            var bytes = ep.Address.GetAddressBytes();
            writer.WriteVarInt32(bytes.Length);
            writer.Write(bytes);

            // Port
            writer.WriteVarUInt32((uint)ep.Port);

            // Generation
            writer.WriteInt32(value.Generation);
        }

        private static GrainId ReadGrainId<TInput>(ref Reader<TInput> reader)
        {
            var grainType = IdSpanCodec.ReadRaw(ref reader);
            var grainKey = IdSpanCodec.ReadRaw(ref reader);
            return new GrainId(new GrainType(grainType), grainKey);
        }

        private static void WriteGrainId<TBufferWriter>(ref Writer<TBufferWriter> writer, GrainId value) where TBufferWriter : IBufferWriter<byte>
        {
            IdSpanCodec.WriteRaw(ref writer, value.Type.Value);
            IdSpanCodec.WriteRaw(ref writer, value.Key);
        }

        private static ActivationId ReadActivationId<TInput>(ref Reader<TInput> reader)
        {
            if (reader.ReadByte() == 0)
            {
                return null;
            }

            var n0 = reader.ReadUInt64();
            var n1 = reader.ReadUInt64();
            var typeCodeData = reader.ReadUInt64();
            var keyExt = ReadString(ref reader);
            var key = UniqueKey.NewKey(n0, n1, typeCodeData, keyExt);
            return ActivationId.GetActivationId(key);
        }

        private static void WriteActivationId<TBufferWriter>(ref Writer<TBufferWriter> writer, ActivationId value) where TBufferWriter : IBufferWriter<byte>
        {
            if (value is null || value.Key is null)
            {
                writer.WriteByte(0);
                return;
            }

            writer.WriteByte(1);
            var key = value.Key;
            writer.WriteUInt64(key.N0);
            writer.WriteUInt64(key.N1);
            writer.WriteUInt64(key.TypeCodeData);
            WriteString(ref writer, key.KeyExt);
        }
    }
}
