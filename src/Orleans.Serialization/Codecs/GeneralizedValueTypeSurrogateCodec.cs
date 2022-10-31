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
    public abstract class GeneralizedValueTypeSurrogateCodec<TField, TSurrogate> : IFieldCodec<TField> where TField : struct where TSurrogate : struct
    {
        private static readonly Type CodecFieldType = typeof(TField);
        private readonly IValueSerializer<TSurrogate> _surrogateSerializer;

        /// <summary>
        /// Initializes a new instance of the <see cref="GeneralizedValueTypeSurrogateCodec{TField, TSurrogate}"/> class.
        /// </summary>
        /// <param name="surrogateSerializer">The surrogate serializer.</param>
        protected GeneralizedValueTypeSurrogateCodec(IValueSerializer<TSurrogate> surrogateSerializer)
        {
            _surrogateSerializer = surrogateSerializer;
        }

        /// <inheritdoc/>
        public TField ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            field.EnsureWireTypeTagDelimited();
            ReferenceCodec.MarkValueField(reader.Session);
            TSurrogate surrogate = default;
            _surrogateSerializer.Deserialize(ref reader, ref surrogate);
            var result = ConvertFromSurrogate(ref surrogate);
            return result;
        }

        /// <inheritdoc/>
        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, TField value) where TBufferWriter : IBufferWriter<byte>
        {
            ReferenceCodec.MarkValueField(writer.Session);
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
