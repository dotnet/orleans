using Orleans.Serialization.Activators;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.WireProtocol;
using System;
using System.Buffers;

namespace Orleans.Serialization.Serializers
{
    /// <summary>
    /// Serializer for reference types which can be instantiated.
    /// </summary>
    /// <typeparam name="TField">The field type.</typeparam>
    /// <typeparam name="TBaseCodec">The partial serializer implementation type.</typeparam>
    public sealed class ConcreteTypeSerializer<TField, TBaseCodec> : IFieldCodec<TField> where TField : class where TBaseCodec : IBaseCodec<TField>
    {
        private readonly Type CodecFieldType = typeof(TField);
        private readonly IActivator<TField> _activator;
        private readonly TBaseCodec _serializer;

        /// <summary>
        /// Initializes a new instance of the <see cref="ConcreteTypeSerializer{TField, TBaseCodec}"/> class.
        /// </summary>
        /// <param name="activator">The activator.</param>
        /// <param name="serializer">The serializer.</param>
        public ConcreteTypeSerializer(IActivator<TField> activator, TBaseCodec serializer)
        {
            _activator = activator;
            _serializer = serializer;
        }

        /// <inheritdoc/>
        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, TField value) where TBufferWriter : IBufferWriter<byte>
        {
            if (value is null || value.GetType() == typeof(TField))
            {
                if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
                {
                    return;
                }

                writer.WriteStartObject(fieldIdDelta, expectedType, CodecFieldType);
                _serializer.Serialize(ref writer, value);
                writer.WriteEndObject();
            }
            else
            {
                writer.SerializeUnexpectedType(fieldIdDelta, expectedType, value);
            }
        }

        /// <inheritdoc/>
        public TField ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            if (field.WireType == WireType.Reference)
            {
                return ReferenceCodec.ReadReference<TField, TInput>(ref reader, field);
            }

            var fieldType = field.FieldType;
            if (fieldType is null || fieldType == CodecFieldType)
            {
                var result = _activator.Create();
                ReferenceCodec.RecordObject(reader.Session, result);
                _serializer.Deserialize(ref reader, result);
                return result;
            }

            return reader.DeserializeUnexpectedType<TInput, TField>(ref field);
        }

        public TField ReadValueSealed<TInput>(ref Reader<TInput> reader, Field field)
        {
            if (field.WireType == WireType.Reference)
            {
                return ReferenceCodec.ReadReference<TField, TInput>(ref reader, field);
            }

            var result = _activator.Create();
            ReferenceCodec.RecordObject(reader.Session, result);
            _serializer.Deserialize(ref reader, result);
            return result;
        }
    }
}