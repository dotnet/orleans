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
    public abstract class GeneralizedValueTypeSurrogateCodec<TField, TSurrogate> : IFieldCodec<TField>, IDerivedTypeCodec where TField : struct where TSurrogate : struct
    {
        private static readonly Type CodecFieldType = typeof(TField);
        private readonly IValueSerializer<TSurrogate> _surrogateSerializer;

        protected GeneralizedValueTypeSurrogateCodec(IValueSerializer<TSurrogate> surrogateSerializer)
        {
            _surrogateSerializer = surrogateSerializer;
        }

        public TField ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            ReferenceCodec.MarkValueField(reader.Session);
            TSurrogate surrogate = default;
            _surrogateSerializer.Deserialize(ref reader, ref surrogate);
            var result = ConvertFromSurrogate(ref surrogate);
            return result;
        }

        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, TField value) where TBufferWriter : IBufferWriter<byte>
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteStartObject(fieldIdDelta, expectedType, CodecFieldType);
            TSurrogate surrogate = default;
            ConvertToSurrogate(value, ref surrogate);
            _surrogateSerializer.Serialize(ref writer, ref surrogate);
            writer.WriteEndObject();
        }

        public abstract TField ConvertFromSurrogate(ref TSurrogate surrogate);

        public abstract void ConvertToSurrogate(TField value, ref TSurrogate surrogate);
    }
}
