#nullable enable

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Orleans.Configuration;
using Orleans.Networking.Shared;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Session;
using static Orleans.Runtime.Message;

namespace Orleans.Runtime.Messaging
{
    internal sealed class MessageSerializer : IMessageSerializer
    {
        private const int FramingLength = Message.LENGTH_HEADER_SIZE;
        private const int MessageSizeHint = 4096;
        private readonly Dictionary<Type, ResponseCodec> _rawResponseCodecs = new();
        private readonly CodecProvider _codecProvider;
        private readonly IFieldCodec<GrainAddress> _activationAddressCodec;
        private readonly CachingSiloAddressCodec _readerSiloAddressCodec = new();
        private readonly CachingSiloAddressCodec _writerSiloAddressCodec = new();
        private readonly CachingIdSpanCodec _idSpanCodec = new();
        private readonly SerializerSession _serializationSession;
        private readonly SerializerSession _deserializationSession;
        private readonly MemoryPool<byte> _memoryPool;
        private readonly int _maxHeaderLength;
        private readonly int _maxBodyLength;
        private readonly DictionaryCodec<string, object> _requestContextCodec;
        private object? _bufferWriter;

        public MessageSerializer(
            SerializerSessionPool sessionPool,
            SharedMemoryPool memoryPool,
            MessagingOptions options)
        {
            _serializationSession = sessionPool.GetSession();
            _deserializationSession = sessionPool.GetSession();
            _memoryPool = memoryPool.Pool;
            _maxHeaderLength = options.MaxMessageHeaderSize;
            _maxBodyLength = options.MaxMessageBodySize;
            _codecProvider = sessionPool.CodecProvider;
            _requestContextCodec = OrleansGeneratedCodeHelper.GetService<DictionaryCodec<string, object>>(this, sessionPool.CodecProvider);
            _activationAddressCodec = OrleansGeneratedCodeHelper.GetService<IFieldCodec<GrainAddress>>(this, sessionPool.CodecProvider);
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

                if (bodyLength != 0)
                {
                    _deserializationSession.PartialReset();

                    // Body deserialization is more likely to fail than header deserialization.
                    // Separating the two allows for these kinds of errors to be propagated back to the caller.
                    if (body.IsSingleSegment)
                    {
                        var reader = Reader.Create(body.First.Span, _deserializationSession);
                        ReadBodyObject(message, ref reader);
                    }
                    else
                    {
                        var reader = Reader.Create(body, _deserializationSession);
                        ReadBodyObject(message, ref reader);
                    }
                }

                return (0, headerLength, bodyLength);
            }
            finally
            {
                input = input.Slice(requiredBytes);
                _deserializationSession.FullReset();
            }
        }

        private void ReadBodyObject<TInput>(Message message, ref Reader<TInput> reader)
        {
            var field = reader.ReadFieldHeader();

            if (message.Result == ResponseTypes.Success)
            {
                message.Result = ResponseTypes.None; // reset raw response indicator
                if (!_rawResponseCodecs.TryGetValue(field.FieldType, out var rawCodec))
                    rawCodec = GetRawCodec(field.FieldType);
                message.BodyObject = rawCodec.ReadRaw(ref reader, ref field);
            }
            else
            {
                var bodyCodec = _codecProvider.GetCodec(field.FieldType);
                message.BodyObject = bodyCodec.ReadValue(ref reader, field);
            }
        }

        private ResponseCodec GetRawCodec(Type fieldType)
        {
            var rawCodec = (ResponseCodec)_codecProvider.GetCodec(typeof(Response<>).MakeGenericType(fieldType));
            _rawResponseCodecs.Add(fieldType, rawCodec);
            return rawCodec;
        }

        public (int HeaderLength, int BodyLength) Write<TBufferWriter>(ref TBufferWriter writer, Message message) where TBufferWriter : IBufferWriter<byte>
        {
            var headers = message.Headers;
            IFieldCodec? bodyCodec = null;
            ResponseCodec? rawCodec = null;
            if (message.BodyObject is not null)
            {
                bodyCodec = _codecProvider.GetCodec(message.BodyObject.GetType());
                if (headers.ResponseType is ResponseTypes.None && bodyCodec is ResponseCodec responseCodec)
                {
                    rawCodec = responseCodec;
                    headers.ResponseType = ResponseTypes.Success; // indicates a raw simple response (not wrapped in Response<T>)
                }
            }

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
                Serialize(ref headerWriter, message, headers);
                headerWriter.Commit();

                var headerLength = bufferWriter.CommittedBytes;

                _serializationSession.PartialReset();

                if (message.BodyObject is not null)
                {
                    var bodyWriter = Writer.Create(buffer, _serializationSession);
                    if (rawCodec != null) rawCodec.WriteRaw(ref bodyWriter, message.BodyObject);
                    else bodyCodec!.WriteField(ref bodyWriter, 0, null, message.BodyObject);
                    bodyWriter.Commit();
                }

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
                _serializationSession.FullReset();
            }
        }

        private void ThrowIfLengthsInvalid(int headerLength, int bodyLength)
        {
            if (headerLength <= 0 || headerLength > _maxHeaderLength) ThrowInvalidHeaderLength(headerLength);
            if ((uint)bodyLength > (uint)_maxBodyLength) ThrowInvalidBodyLength(bodyLength);
        }

        private void ThrowInvalidHeaderLength(int headerLength) => throw new OrleansException($"Invalid header size: {headerLength} (max configured value is {_maxHeaderLength}, see {nameof(MessagingOptions.MaxMessageHeaderSize)})");
        private void ThrowInvalidBodyLength(int bodyLength) => throw new OrleansException($"Invalid body size: {bodyLength} (max configured value is {_maxBodyLength}, see {nameof(MessagingOptions.MaxMessageBodySize)})");

        private void Serialize<TBufferWriter>(ref Writer<TBufferWriter> writer, Message value, PackedHeaders headers) where TBufferWriter : IBufferWriter<byte>
        {
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
                _idSpanCodec.WriteRaw(ref writer, value.InterfaceType.Value);
            }

            if (headers.HasFlag(MessageFlags.HasInterfaceVersion))
            {
                writer.WriteVarUInt32(value.InterfaceVersion);
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
                var interfaceTypeSpan = _idSpanCodec.ReadRaw(ref reader);
                result.InterfaceType = new GrainInterfaceType(interfaceTypeSpan);
            }

            if (headers.HasFlag(MessageFlags.HasInterfaceVersion))
            {
                result.InterfaceVersion = (ushort)reader.ReadVarUInt32();
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
            var n = (int)reader.ReadVarUInt32();
            if (n > 0)
            {
                var list = new List<GrainAddress>(n);
                for (int i = 0; i < n; i++)
                {
                    list.Add(_activationAddressCodec.ReadValue(ref reader, reader.ReadFieldHeader()));
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
                _activationAddressCodec.WriteField(ref writer, 0, typeof(GrainAddress), entry);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static string? ReadString<TInput>(ref Reader<TInput> reader)
        {
            var length = (int)reader.ReadVarUInt32() - 1;
            if (length <= 0)
            {
                if (length < 0)
                {
                    return null;
                }

                return string.Empty;
            }

            string result;
            if (reader.TryReadBytes(length, out var span))
            {
                result = Encoding.UTF8.GetString(span);
            }
            else
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
                writer.WriteVarUInt32(0);
                return;
            }

            var numBytes = Encoding.UTF8.GetByteCount(value);
            writer.WriteVarUInt32((uint)numBytes + 1);
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
        }

        private static void WriteRequestContext<TBufferWriter>(ref Writer<TBufferWriter> writer, Dictionary<string, object> value) where TBufferWriter : IBufferWriter<byte>
        {
            writer.WriteVarUInt32((uint)value.Count);
            foreach (var entry in value)
            {
                WriteString(ref writer, entry.Key);
                ObjectCodec.WriteField(ref writer, 0, null, entry.Value);
            }
        }

        private static Dictionary<string, object> ReadRequestContext<TInput>(ref Reader<TInput> reader)
        {
            var size = (int)reader.ReadVarUInt32();
            var result = new Dictionary<string, object>(size);
            for (var i = 0; i < size; i++)
            {
                var key = ReadString(ref reader);
                var value = ObjectCodec.ReadValue(ref reader, reader.ReadFieldHeader());

                Debug.Assert(key is not null);
                result.Add(key, value);
            }

            return result;
        }

        private GrainId ReadGrainId<TInput>(ref Reader<TInput> reader)
        {
            var grainType = _idSpanCodec.ReadRaw(ref reader);
            var grainKey = IdSpanCodec.ReadRaw(ref reader);
            return new GrainId(new GrainType(grainType), grainKey);
        }

        private void WriteGrainId<TBufferWriter>(ref Writer<TBufferWriter> writer, GrainId value) where TBufferWriter : IBufferWriter<byte>
        {
            _idSpanCodec.WriteRaw(ref writer, value.Type.Value);
            IdSpanCodec.WriteRaw(ref writer, value.Key);
        }
    }
}
