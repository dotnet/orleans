using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.WireProtocol;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Orleans.Serialization.Codecs
{
    [RegisterSerializer]
    public sealed class KeyValuePairCodec<TKey, TValue> : IFieldCodec<KeyValuePair<TKey, TValue>>
    {
        private readonly IFieldCodec<TKey> _keyCodec;
        private readonly IFieldCodec<TValue> _valueCodec;
        private static readonly Type CodecKeyType = typeof(TKey);
        private static readonly Type CodecValueType = typeof(TValue);

        public KeyValuePairCodec(IFieldCodec<TKey> keyCodec, IFieldCodec<TValue> valueCodec)
        {
            _keyCodec = OrleansGeneratedCodeHelper.UnwrapService(this, keyCodec);
            _valueCodec = OrleansGeneratedCodeHelper.UnwrapService(this, valueCodec);
        }

        void IFieldCodec<KeyValuePair<TKey, TValue>>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer,
            uint fieldIdDelta,
            Type expectedType,
            KeyValuePair<TKey, TValue> value)
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeader(fieldIdDelta, expectedType, value.GetType(), WireType.TagDelimited);

            _keyCodec.WriteField(ref writer, 0, CodecKeyType, value.Key);
            _valueCodec.WriteField(ref writer, 1, CodecValueType, value.Value);

            writer.WriteEndObject();
        }

        public KeyValuePair<TKey, TValue> ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            if (field.WireType != WireType.TagDelimited)
            {
                ThrowUnsupportedWireTypeException(field);
            }

            ReferenceCodec.MarkValueField(reader.Session);
            var key = default(TKey);
            var value = default(TValue);
            uint fieldId = 0;
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
                        key = _keyCodec.ReadValue(ref reader, header);
                        break;
                    case 1:
                        value = _valueCodec.ReadValue(ref reader, header);
                        break;
                    default:
                        reader.ConsumeUnknownField(header);
                        break;
                }
            }

            return new KeyValuePair<TKey, TValue>(key, value);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowUnsupportedWireTypeException(Field field) => throw new UnsupportedWireTypeException(
            $"Only a {nameof(WireType)} value of {WireType.TagDelimited} is supported. {field}");
    }

    [RegisterCopier]
    public sealed class KeyValuePairCopier<TKey, TValue> : IDeepCopier<KeyValuePair<TKey, TValue>>
    {
        private readonly IDeepCopier<TKey> _keyCopier;
        private readonly IDeepCopier<TValue> _valueCopier;

        public KeyValuePairCopier(IDeepCopier<TKey> keyCopier, IDeepCopier<TValue> valueCopier)
        {
            _keyCopier = keyCopier;
            _valueCopier = valueCopier;
        }

        public KeyValuePair<TKey, TValue> DeepCopy(KeyValuePair<TKey, TValue> input, CopyContext context) => new(_keyCopier.DeepCopy(input.Key, context), _valueCopier.DeepCopy(input.Value, context));
    }
}