using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.WireProtocol;
using System;

namespace Orleans.Serialization.Serializers
{
    /// <summary>
    /// Serializer for value types.
    /// </summary>
    /// <typeparam name="TField">The field type.</typeparam>
    /// <typeparam name="TValueSerializer">The value-type serializer implementation type.</typeparam>
    public sealed class ValueSerializer<TField, TValueSerializer> : IFieldCodec<TField> where TField : struct where TValueSerializer : IValueSerializer<TField>
    {
        private static readonly Type CodecFieldType = typeof(TField);
        private readonly TValueSerializer _serializer;

        public ValueSerializer(TValueSerializer serializer)
        {
            _serializer = serializer;
        }

        void IFieldCodec<TField>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, TField value)
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteStartObject(fieldIdDelta, expectedType, CodecFieldType);
            _serializer.Serialize(ref writer, ref value);
            writer.WriteEndObject();
        }

        TField IFieldCodec<TField>.ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            ReferenceCodec.MarkValueField(reader.Session);
            var value = default(TField);
            _serializer.Deserialize(ref reader, ref value);
            return value;
        }
    }
}