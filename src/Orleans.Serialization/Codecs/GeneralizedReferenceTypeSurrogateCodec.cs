using Orleans.Serialization.Buffers;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.WireProtocol;
using System;
using System.Buffers;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Surrogate serializer for <typeparamref name="TField"/> and all sub-types.
    /// </summary>
    /// <typeparam name="TField">The type which the implementation of this class supports.</typeparam>
    /// <typeparam name="TSurrogate">The surrogate type serialized in place of <typeparamref name="TField"/>.</typeparam>
    public abstract class GeneralizedReferenceTypeSurrogateCodec<TField, TSurrogate> : IFieldCodec<TField>, IDerivedTypeCodec where TField : class where TSurrogate : struct
    {
        private static readonly Type CodecFieldType = typeof(TField);
        private readonly IValueSerializer<TSurrogate> _surrogateSerializer;

        /// <summary>
        /// Initializes a new instance of the <see cref="GeneralizedReferenceTypeSurrogateCodec{TField, TSurrogate}"/> class.
        /// </summary>
        /// <param name="surrogateSerializer">The surrogate serializer.</param>
        protected GeneralizedReferenceTypeSurrogateCodec(IValueSerializer<TSurrogate> surrogateSerializer)
        {
            _surrogateSerializer = surrogateSerializer;
        }

        /// <inheritdoc/>
        public TField ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            if (field.WireType == WireType.Reference)
            {
                return ReferenceCodec.ReadReference<TField, TInput>(ref reader, field);
            }

            var placeholderReferenceId = ReferenceCodec.CreateRecordPlaceholder(reader.Session);
            TSurrogate surrogate = default;
            _surrogateSerializer.Deserialize(ref reader, ref surrogate);
            var result = ConvertFromSurrogate(ref surrogate);
            ReferenceCodec.RecordObject(reader.Session, result, placeholderReferenceId);
            return result;
        }

        /// <inheritdoc/>
        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, TField value) where TBufferWriter : IBufferWriter<byte>
        {
            if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
            {
                return;
            }

            writer.WriteStartObject(fieldIdDelta, expectedType, CodecFieldType);
            TSurrogate surrogate = default;
            ConvertToSurrogate(value, ref surrogate);
            _surrogateSerializer.Serialize(ref writer, ref surrogate);
            writer.WriteEndObject();
        }

        /// <summary>
        /// Converts a value from the surrogate type to the field type.
        /// </summary>
        /// <param name="surrogate">The surrogate.</param>
        /// <returns>The value.</returns>
        public abstract TField ConvertFromSurrogate(ref TSurrogate surrogate);

        /// <summary>
        /// Converts a value to the surrogate type.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <param name="surrogate">The surrogate.</param>
        public abstract void ConvertToSurrogate(TField value, ref TSurrogate surrogate);
    }
}
