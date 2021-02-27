using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.WireProtocol;
using Newtonsoft.Json;
using System;
using System.Runtime.CompilerServices;
using System.Text;

namespace Orleans.Serialization
{
    [WellKnownAlias("NewtonsoftJson")]
    public class NewtonsoftJsonCodec : IGeneralizedCodec
    {
        private static readonly Type SelfType = typeof(NewtonsoftJsonCodec);
        private readonly Func<Type, bool> _isSupportedFunc;
        private readonly JsonSerializerSettings _settings;

        public NewtonsoftJsonCodec(
            JsonSerializerSettings settings = null,
            Func<Type, bool> isSupportedFunc = null)
        {
            _settings = settings ?? new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.All,
                TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Full
            };
            _isSupportedFunc = isSupportedFunc ?? (_ => true);
        }

        void IFieldCodec<object>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, object value)
        {
            if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
            {
                return;
            }

            var result = JsonConvert.SerializeObject(value, _settings);

            // The schema type when serializing the field is the type of the codec.
            // In practice it could be any unique type as long as this codec is registered as the handler.
            // By checking against the codec type in IsSupportedType, the codec could also just be registered as an IGenericCodec.
            // Note that the codec is responsible for serializing the type of the value itself.
            writer.WriteFieldHeader(fieldIdDelta, expectedType, SelfType, WireType.LengthPrefixed);

            // TODO: NoAlloc
            var bytes = Encoding.UTF8.GetBytes(result);
            writer.WriteVarUInt32((uint)bytes.Length);
            writer.Write(bytes);
        }

        object IFieldCodec<object>.ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            if (field.WireType == WireType.Reference)
            {
                return ReferenceCodec.ReadReference<object, TInput>(ref reader, field);
            }

            if (field.WireType != WireType.LengthPrefixed)
            {
                ThrowUnsupportedWireTypeException(field);
            }

            var length = reader.ReadVarUInt32();
            var bytes = reader.ReadBytes(length);

            // TODO: NoAlloc
            var resultString = Encoding.UTF8.GetString(bytes);
            var result = JsonConvert.DeserializeObject(resultString, _settings);
            ReferenceCodec.RecordObject(reader.Session, result);
            return result;
        }

        public bool IsSupportedType(Type type) => type == SelfType || _isSupportedFunc(type);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowUnsupportedWireTypeException(Field field) => throw new UnsupportedWireTypeException(
            $"Only a {nameof(WireType)} value of {WireType.LengthPrefixed} is supported for JSON fields. {field}");
    }
}