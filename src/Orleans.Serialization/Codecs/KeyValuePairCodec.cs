using System;
using System.Buffers;
using System.Collections.Generic;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for <see cref="KeyValuePair{TKey, TValue}"/>.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    [RegisterSerializer]
    public sealed class KeyValuePairCodec<TKey, TValue> : IFieldCodec<KeyValuePair<TKey, TValue>>
    {
        private readonly IFieldCodec<TKey> _keyCodec;
        private readonly IFieldCodec<TValue> _valueCodec;
        private readonly Type CodecKeyType = typeof(TKey);
        private readonly Type CodecValueType = typeof(TValue);

        /// <summary>
        /// Initializes a new instance of the <see cref="KeyValuePairCodec{TKey, TValue}"/> class.
        /// </summary>
        /// <param name="keyCodec">The key codec.</param>
        /// <param name="valueCodec">The value codec.</param>
        public KeyValuePairCodec(IFieldCodec<TKey> keyCodec, IFieldCodec<TValue> valueCodec)
        {
            _keyCodec = OrleansGeneratedCodeHelper.UnwrapService(this, keyCodec);
            _valueCodec = OrleansGeneratedCodeHelper.UnwrapService(this, valueCodec);
        }

        /// <inheritdoc/>
        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer,
            uint fieldIdDelta,
            Type expectedType,
            KeyValuePair<TKey, TValue> value) where TBufferWriter : IBufferWriter<byte>
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeader(fieldIdDelta, expectedType, value.GetType(), WireType.TagDelimited);

            _keyCodec.WriteField(ref writer, 0, CodecKeyType, value.Key);
            _valueCodec.WriteField(ref writer, 1, CodecValueType, value.Value);

            writer.WriteEndObject();
        }

        /// <inheritdoc/>
        public KeyValuePair<TKey, TValue> ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            field.EnsureWireTypeTagDelimited();
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
    }

    /// <summary>
    /// Copier for <see cref="KeyValuePair{TKey, TValue}"/>.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    [RegisterCopier]
    public sealed class KeyValuePairCopier<TKey, TValue> : IDeepCopier<KeyValuePair<TKey, TValue>>, IOptionalDeepCopier
    {
        private readonly IDeepCopier<TKey> _keyCopier;
        private readonly IDeepCopier<TValue> _valueCopier;

        /// <summary>
        /// Initializes a new instance of the <see cref="KeyValuePairCopier{TKey, TValue}"/> class.
        /// </summary>
        /// <param name="keyCopier">The key copier.</param>
        /// <param name="valueCopier">The value copier.</param>
        public KeyValuePairCopier(IDeepCopier<TKey> keyCopier, IDeepCopier<TValue> valueCopier)
        {
            _keyCopier = OrleansGeneratedCodeHelper.GetOptionalCopier(keyCopier);
            _valueCopier = OrleansGeneratedCodeHelper.GetOptionalCopier(valueCopier);
        }

        public bool IsShallowCopyable() => _keyCopier is null && _valueCopier is null;

        object IDeepCopier.DeepCopy(object input, CopyContext context) => IsShallowCopyable() ? input : DeepCopy((KeyValuePair<TKey, TValue>)input, context);

        /// <inheritdoc/>
        public KeyValuePair<TKey, TValue> DeepCopy(KeyValuePair<TKey, TValue> input, CopyContext context)
        {
            return new(_keyCopier is null ? input.Key : _keyCopier.DeepCopy(input.Key, context),
                _valueCopier is null ? input.Value : _valueCopier.DeepCopy(input.Value, context));
        }
    }
}