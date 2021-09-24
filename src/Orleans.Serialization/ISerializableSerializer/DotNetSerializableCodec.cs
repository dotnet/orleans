using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.TypeSystem;
using Orleans.Serialization.WireProtocol;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.Serialization;
using System.Security;

namespace Orleans.Serialization
{
    [WellKnownAlias("ISerializable")]
    public class DotNetSerializableCodec : IGeneralizedCodec
    {
        public static readonly Type CodecType = typeof(DotNetSerializableCodec);
        private static readonly Type SerializableType = typeof(ISerializable);
        private readonly SerializationCallbacksFactory _serializationCallbacks;
        private readonly Func<Type, Action<object, SerializationInfo, StreamingContext>> _createConstructorDelegate;
        private readonly ConcurrentDictionary<Type, Action<object, SerializationInfo, StreamingContext>> _constructors = new();
        private readonly IFormatterConverter _formatterConverter;
        private readonly StreamingContext _streamingContext;
        private readonly SerializationEntryCodec _entrySerializer;
        private readonly TypeConverter _typeConverter;
        private readonly ValueTypeSerializerFactory _valueTypeSerializerFactory;

        public DotNetSerializableCodec(TypeConverter typeResolver)
        {
            _streamingContext = new StreamingContext(StreamingContextStates.All);
            _typeConverter = typeResolver;
            _entrySerializer = new SerializationEntryCodec();
            _serializationCallbacks = new SerializationCallbacksFactory();
            _formatterConverter = new FormatterConverter();
            var constructorFactory = new SerializationConstructorFactory();
            _createConstructorDelegate = constructorFactory.GetSerializationConstructorDelegate;

            _valueTypeSerializerFactory = new ValueTypeSerializerFactory(
                _entrySerializer,
                constructorFactory,
                _serializationCallbacks,
                _formatterConverter,
                _streamingContext);
        }

        [SecurityCritical]
        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, object value) where TBufferWriter : IBufferWriter<byte>
        {
            if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
            {
                return;
            }

            var type = value.GetType();
            writer.WriteFieldHeader(fieldIdDelta, expectedType, CodecType, WireType.TagDelimited);
            if (type.IsValueType)
            {
                var serializer = _valueTypeSerializerFactory.GetSerializer(type);
                serializer.WriteValue(ref writer, value);
            }
            else
            {
                WriteObject(ref writer, type, value);
            }

            writer.WriteEndObject();
        }

        [SecurityCritical]
        public object ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            if (field.WireType == WireType.Reference)
            {
                return ReferenceCodec.ReadReference<object, TInput>(ref reader, field);
            }

            var placeholderReferenceId = ReferenceCodec.CreateRecordPlaceholder(reader.Session);
            Type type;
            var header = reader.ReadFieldHeader();
            var flags = Int32Codec.ReadValue(ref reader, header);
            if (flags == 1)
            {
                // This is an exception type, so deserialize it as an exception.
                header = reader.ReadFieldHeader();
                var typeName = StringCodec.ReadValue(ref reader, header);
                if (!_typeConverter.TryParse(typeName, out type))
                {
                    return ReadFallbackException(ref reader, typeName, placeholderReferenceId);
                }
            }
            else
            {
                header = reader.ReadFieldHeader();
                type = TypeSerializerCodec.ReadValue(ref reader, header);
            }

            if (type.IsValueType)
            {
                var serializer = _valueTypeSerializerFactory.GetSerializer(type);
                return serializer.ReadValue(ref reader, type);
            }

            return ReadObject(ref reader, type, placeholderReferenceId);
        }

        private object ReadFallbackException<TInput>(ref Reader<TInput> reader, string typeName, uint placeholderReferenceId)
        {
            // Deserialize into a fallback type for unknown exceptions. This means that missing fields will not be represented.
            var result = (UnavailableExceptionFallbackException)ReadObject(ref reader, typeof(UnavailableExceptionFallbackException), placeholderReferenceId);
            result.ExceptionType = typeName;
            return result;
        }

        private object ReadObject<TInput>(ref Reader<TInput> reader, Type type, uint placeholderReferenceId)
        {
            var callbacks = _serializationCallbacks.GetReferenceTypeCallbacks(type);

            var info = new SerializationInfo(type, _formatterConverter);
            var result = FormatterServices.GetUninitializedObject(type);
            ReferenceCodec.RecordObject(reader.Session, result, placeholderReferenceId);
            callbacks.OnDeserializing?.Invoke(result, _streamingContext);

            uint fieldId = 0;
            while (true)
            {
                var header = reader.ReadFieldHeader();
                if (header.IsEndBaseOrEndObject)
                {
                    break;
                }

                fieldId += header.FieldIdDelta;
                if (fieldId == 1)
                {
                    var entry = _entrySerializer.ReadValue(ref reader, header);
                    if (entry.ObjectType is { } entryType)
                    {
                        info.AddValue(entry.Name, entry.Value, entryType);
                    }
                    else
                    {
                        info.AddValue(entry.Name, entry.Value);
                    }
                }
                else
                {
                    reader.ConsumeUnknownField(header);
                }
            }

            var constructor = _constructors.GetOrAdd(info.ObjectType, _createConstructorDelegate);
            constructor(result, info, _streamingContext);
            callbacks.OnDeserialized?.Invoke(result, _streamingContext);
            if (result is IDeserializationCallback callback)
            {
                callback.OnDeserialization(_streamingContext.Context);
            }

            return result;
        }


        private void WriteObject<TBufferWriter>(ref Writer<TBufferWriter> writer, Type type, object value) where TBufferWriter : IBufferWriter<byte>
        {
            var callbacks = _serializationCallbacks.GetReferenceTypeCallbacks(type);
            var info = new SerializationInfo(type, _formatterConverter);

            // Serialize the type name according to the value populated in the SerializationInfo.
            if (value is Exception)
            {
                // Indicate that this is an exception
                Int32Codec.WriteField(ref writer, 0, typeof(int), 1);

                // For exceptions, the type is serialized as a string to facilitate safe deserialization.
                var typeName = _typeConverter.Format(info.ObjectType);
                StringCodec.WriteField(ref writer, 1, typeof(string), typeName);
            }
            else
            {
                // Indicate that this is not an exception
                Int32Codec.WriteField(ref writer, 0, typeof(int), 0);

                TypeSerializerCodec.WriteField(ref writer, 1, typeof(Type), info.ObjectType);
            }

            callbacks.OnSerializing?.Invoke(value, _streamingContext);
            ((ISerializable)value).GetObjectData(info, _streamingContext);

            var first = true;
            foreach (var field in info)
            {
                var surrogate = new SerializationEntrySurrogate
                {
                    Name = field.Name,
                    Value = field.Value,
                    ObjectType = field.ObjectType
                };

                _entrySerializer.WriteField(ref writer, first ? 1 : (uint)0, SerializationEntryCodec.SerializationEntryType, surrogate);
                if (first)
                {
                    first = false;
                }
            }

            callbacks.OnSerialized?.Invoke(value, _streamingContext);
        }

        [SecurityCritical]
        public bool IsSupportedType(Type type) =>
            type == CodecType || typeof(Exception).IsAssignableFrom(type) || (SerializableType.IsAssignableFrom(type) && SerializationConstructorFactory.HasSerializationConstructor(type));
    }
}