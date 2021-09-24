using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using System;
using System.Buffers;
using System.Runtime.Serialization;
using System.Security;

namespace Orleans.Serialization
{
    internal abstract class ValueTypeSerializer
    {
        public abstract void WriteValue<TBufferWriter>(ref Writer<TBufferWriter> writer, object value) where TBufferWriter : IBufferWriter<byte>;
        public abstract object ReadValue<TInput>(ref Reader<TInput> reader, Type type);
    }

    /// <summary>
    /// Serializer for ISerializable value types.
    /// </summary>
    /// <typeparam name="T">The type which this serializer can serialize.</typeparam>
    internal class ValueTypeSerializer<T> : ValueTypeSerializer where T : struct
    {
        public delegate void ValueConstructor(ref T value, SerializationInfo info, StreamingContext context);

        public delegate void SerializationCallback(ref T value, StreamingContext context);

        private static readonly Type Type = typeof(T);

        private readonly ValueConstructor _constructor;
        private readonly SerializationCallbacksFactory.SerializationCallbacks<SerializationCallback> _callbacks;

        private readonly IFormatterConverter _formatterConverter;
        private readonly StreamingContext _streamingContext;
        private readonly SerializationEntryCodec _entrySerializer;

        [SecurityCritical]
        public ValueTypeSerializer(
            ValueConstructor constructor,
            SerializationCallbacksFactory.SerializationCallbacks<SerializationCallback> callbacks,
            SerializationEntryCodec entrySerializer,
            StreamingContext streamingContext,
            IFormatterConverter formatterConverter)
        {
            _constructor = constructor;
            _callbacks = callbacks;
            _entrySerializer = entrySerializer;
            _streamingContext = streamingContext;
            _formatterConverter = formatterConverter;
        }

        [SecurityCritical]
        public override void WriteValue<TBufferWriter>(ref Writer<TBufferWriter> writer, object value)
        {
            var item = (T)value;
            _callbacks.OnSerializing?.Invoke(ref item, _streamingContext);

            var info = new SerializationInfo(Type, _formatterConverter);
            ((ISerializable)value).GetObjectData(info, _streamingContext);

            Int32Codec.WriteField(ref writer, 0, typeof(int), 0);
            TypeSerializerCodec.WriteField(ref writer, 1, typeof(Type), info.ObjectType);

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

            _callbacks.OnSerialized?.Invoke(ref item, _streamingContext);
        }

        [SecurityCritical]
        public override object ReadValue<TInput>(ref Reader<TInput> reader, Type type)
        {
            var info = new SerializationInfo(Type, _formatterConverter);
            T result = default;

            _callbacks.OnDeserializing?.Invoke(ref result, _streamingContext);

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

            _constructor(ref result, info, _streamingContext);
            _callbacks.OnDeserialized?.Invoke(ref result, _streamingContext);
            if (result is IDeserializationCallback callback)
            {
                callback.OnDeserialization(_streamingContext.Context);
            }

            return result;
        }
    }
}