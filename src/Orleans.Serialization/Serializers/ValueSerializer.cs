using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.WireProtocol;
using System;
using System.Buffers;

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

        /// <summary>
        /// Initializes a new instance of the <see cref="ValueSerializer{TField, TValueSerializer}"/> class.
        /// </summary>
        /// <param name="serializer">The serializer.</param>
        public ValueSerializer(TValueSerializer serializer)
        {
            _serializer = serializer;
        }

        /// <inheritdoc/>
        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, TField value) where TBufferWriter : IBufferWriter<byte>
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteStartObject(fieldIdDelta, expectedType, CodecFieldType);
            _serializer.Serialize(ref writer, ref value);
            writer.WriteEndObject();
        }

        /// <inheritdoc/>
        public TField ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            ReferenceCodec.MarkValueField(reader.Session);
            var value = default(TField);
            _serializer.Deserialize(ref reader, ref value);
            return value;
        }
    }
}