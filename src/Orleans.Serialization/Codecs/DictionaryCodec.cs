using System;
using System.Buffers;
using System.Collections.Generic;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.Session;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for <see cref="Dictionary{TKey, TValue}"/>.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    [RegisterSerializer]
    public sealed class DictionaryCodec<TKey, TValue> : IFieldCodec<Dictionary<TKey, TValue>>
    {
        private readonly Type _keyFieldType = typeof(TKey);
        private readonly Type _valueFieldType = typeof(TValue);

        private readonly IFieldCodec<TKey> _keyCodec;
        private readonly IFieldCodec<TValue> _valueCodec;
        private readonly IFieldCodec<IEqualityComparer<TKey>> _comparerCodec;

        /// <summary>
        /// Initializes a new instance of the <see cref="DictionaryCodec{TKey, TValue}"/> class.
        /// </summary>
        /// <param name="keyCodec">The key codec.</param>
        /// <param name="valueCodec">The value codec.</param>
        /// <param name="comparerCodec">The comparer codec.</param>
        public DictionaryCodec(
            IFieldCodec<TKey> keyCodec,
            IFieldCodec<TValue> valueCodec,
            IFieldCodec<IEqualityComparer<TKey>> comparerCodec)
        {
            _keyCodec = OrleansGeneratedCodeHelper.UnwrapService(this, keyCodec);
            _valueCodec = OrleansGeneratedCodeHelper.UnwrapService(this, valueCodec);
            _comparerCodec = OrleansGeneratedCodeHelper.UnwrapService(this, comparerCodec);
        }

        /// <inheritdoc/>
        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, Dictionary<TKey, TValue> value) where TBufferWriter : IBufferWriter<byte>
        {
            if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
            {
                return;
            }

            writer.WriteFieldHeader(fieldIdDelta, expectedType, value.GetType(), WireType.TagDelimited);

            if (value.Comparer != EqualityComparer<TKey>.Default)
            {
                _comparerCodec.WriteField(ref writer, 0, typeof(IEqualityComparer<TKey>), value.Comparer);
            }

            if (value.Count > 0)
            {
                UInt32Codec.WriteField(ref writer, 1, UInt32Codec.CodecFieldType, (uint)value.Count);
                uint innerFieldIdDelta = 1;
                foreach (var element in value)
                {
                    _keyCodec.WriteField(ref writer, innerFieldIdDelta, _keyFieldType, element.Key);
                    _valueCodec.WriteField(ref writer, 0, _valueFieldType, element.Value);
                    innerFieldIdDelta = 0;
                }
            }

            writer.WriteEndObject();
        }

        /// <inheritdoc/>
        public Dictionary<TKey, TValue> ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            if (field.WireType == WireType.Reference)
            {
                return ReferenceCodec.ReadReference<Dictionary<TKey, TValue>, TInput>(ref reader, field);
            }

            field.EnsureWireTypeTagDelimited();

            var placeholderReferenceId = ReferenceCodec.CreateRecordPlaceholder(reader.Session);
            TKey key = default;
            var valueExpected = false;
            Dictionary<TKey, TValue> result = null;
            IEqualityComparer<TKey> comparer = null;
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
                        comparer = _comparerCodec.ReadValue(ref reader, header);
                        break;
                    case 1:
                        var length = (int)UInt32Codec.ReadValue(ref reader, header);
                        if (length > 10240 && length > reader.Length)
                        {
                            ThrowInvalidSizeException(length);
                        }

                        result = CreateInstance(length, comparer, reader.Session, placeholderReferenceId);
                        break;
                    case 2:
                        if (result is null)
                            ThrowLengthFieldMissing();

                        if (!valueExpected)
                        {
                            key = _keyCodec.ReadValue(ref reader, header);
                            valueExpected = true;
                        }
                        else
                        {
                            result.Add(key, _valueCodec.ReadValue(ref reader, header));
                            valueExpected = false;
                        }
                        break;
                    default:
                        reader.ConsumeUnknownField(header);
                        break;
                }
            }

            result ??= CreateInstance(0, comparer, reader.Session, placeholderReferenceId);
            return result;
        }

        private Dictionary<TKey, TValue> CreateInstance(int length, IEqualityComparer<TKey> comparer, SerializerSession session, uint placeholderReferenceId)
        {
            var result = new Dictionary<TKey, TValue>(length, comparer);
            ReferenceCodec.RecordObject(session, result, placeholderReferenceId);
            return result;
        }

        private static void ThrowInvalidSizeException(int length) => throw new IndexOutOfRangeException(
            $"Declared length of {typeof(Dictionary<TKey, TValue>)}, {length}, is greater than total length of input.");

        private static void ThrowLengthFieldMissing() => throw new RequiredFieldMissingException("Serialized dictionary is missing its length field.");
    }

    /// <summary>
    /// Copier for <see cref="Dictionary{TKey, TValue}"/>.
    /// </summary>
    /// <typeparam name="TKey">The type of the t key.</typeparam>
    /// <typeparam name="TValue">The type of the t value.</typeparam>
    [RegisterCopier]
    public sealed class DictionaryCopier<TKey, TValue> : IDeepCopier<Dictionary<TKey, TValue>>, IBaseCopier<Dictionary<TKey, TValue>>
    {
        private readonly IDeepCopier<TKey> _keyCopier;
        private readonly IDeepCopier<TValue> _valueCopier;

        /// <summary>
        /// Initializes a new instance of the <see cref="DictionaryCopier{TKey, TValue}"/> class.
        /// </summary>
        /// <param name="keyCopier">The key copier.</param>
        /// <param name="valueCopier">The value copier.</param>
        public DictionaryCopier(IDeepCopier<TKey> keyCopier, IDeepCopier<TValue> valueCopier)
        {
            _keyCopier = keyCopier;
            _valueCopier = valueCopier;
        }

        /// <inheritdoc/>
        public Dictionary<TKey, TValue> DeepCopy(Dictionary<TKey, TValue> input, CopyContext context)
        {
            if (context.TryGetCopy<Dictionary<TKey, TValue>>(input, out var result))
            {
                return result;
            }

            if (input.GetType() != typeof(Dictionary<TKey, TValue>))
            {
                return context.DeepCopy(input);
            }

            result = new Dictionary<TKey, TValue>(input.Count, input.Comparer);
            context.RecordCopy(input, result);
            foreach (var pair in input)
            {
                result[_keyCopier.DeepCopy(pair.Key, context)] = _valueCopier.DeepCopy(pair.Value, context);
            }

            return result;
        }

        /// <inheritdoc/>
        public void DeepCopy(Dictionary<TKey, TValue> input, Dictionary<TKey, TValue> output, CopyContext context)
        {
            foreach (var pair in input)
            {
                output[_keyCopier.DeepCopy(pair.Key, context)] = _valueCopier.DeepCopy(pair.Value, context);
            }
        }
    }
}