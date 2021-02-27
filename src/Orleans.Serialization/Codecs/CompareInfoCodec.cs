using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.WireProtocol;
using System;
using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Orleans.Serialization.Codecs
{
    [RegisterSerializer]
    public sealed class CompareInfoCodec : IFieldCodec<CompareInfo>, IGeneralizedCodec
    {
        public bool IsSupportedType(Type type) => typeof(CompareInfo).IsAssignableFrom(type);
        public CompareInfo ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            if (field.WireType == WireType.Reference)
            {
                return ReferenceCodec.ReadReference<CompareInfo, TInput>(ref reader, field);
            }

            if (field.WireType != WireType.TagDelimited)
            {
                ThrowUnsupportedWireTypeException(field);
            }

            var placeholderReferenceId = ReferenceCodec.CreateRecordPlaceholder(reader.Session);
            uint fieldId = 0;
            string name = null;
            while (true)
            {
                var header = reader.ReadFieldHeader();
                if (header.IsEndBaseOrEndObject)
                {
                    break;
                }

                fieldId += header.FieldIdDelta;
                switch (fieldId)
                {
                    case 0:
                        name = StringCodec.ReadValue(ref reader, header);
                        break;
                    case 1:
                        break;
                    default:
                        reader.ConsumeUnknownField(header);
                        break;
                }
            }

            var result = CompareInfo.GetCompareInfo(name);
            ReferenceCodec.RecordObject(reader.Session, result, placeholderReferenceId);
            return result;
        }

        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, CompareInfo value) where TBufferWriter : IBufferWriter<byte>
        {
            if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
            {
                return;
            }

            writer.WriteFieldHeader(fieldIdDelta, expectedType, value.GetType(), WireType.TagDelimited);
            StringCodec.WriteField(ref writer, 0, StringCodec.CodecFieldType, value.Name);
            writer.WriteEndObject();
        }

        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, object value) where TBufferWriter : IBufferWriter<byte> => WriteField(ref writer, fieldIdDelta, expectedType, value as CompareInfo);
        object IFieldCodec<object>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => ReadValue(ref reader, field);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowUnsupportedWireTypeException(Field field) => throw new UnsupportedWireTypeException(
            $"Only a {nameof(WireType)} value of {WireType.TagDelimited} is supported for {nameof(CompareInfo)} fields. {field}");
    }

    [RegisterCopier]
    public sealed class CompareInfoCopier : IDeepCopier<CompareInfo>, IGeneralizedCopier
    {
        public CompareInfo DeepCopy(CompareInfo input, CopyContext context) => input;
        public object DeepCopy(object input, CopyContext context) => input;
        public bool IsSupportedType(Type type) => typeof(CompareInfo).IsAssignableFrom(type);
    }
}