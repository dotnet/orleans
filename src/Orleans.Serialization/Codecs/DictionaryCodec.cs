using Orleans.Serialization.Activators;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Session;
using Orleans.Serialization.WireProtocol;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

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
        private static readonly Type CodecFieldType = typeof(KeyValuePair<TKey, TValue>);

        private readonly IFieldCodec<KeyValuePair<TKey, TValue>> _pairCodec;
        private readonly IFieldCodec<IEqualityComparer<TKey>> _comparerCodec;
        private readonly DictionaryActivator<TKey, TValue> _activator;

        /// <summary>
        /// Initializes a new instance of the <see cref="DictionaryCodec{TKey, TValue}"/> class.
        /// </summary>
        /// <param name="pairCodec">The key value pair codec.</param>
        /// <param name="comparerCodec">The comparer codec.</param>
        /// <param name="activator">The activator.</param>
        public DictionaryCodec(
            IFieldCodec<KeyValuePair<TKey, TValue>> pairCodec,
            IFieldCodec<IEqualityComparer<TKey>> comparerCodec,
            DictionaryActivator<TKey, TValue> activator)
        {
            _pairCodec = pairCodec;
            _comparerCodec = comparerCodec;
            _activator = activator;
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

            Int32Codec.WriteField(ref writer, 1, typeof(int), value.Count);

            uint innerFieldIdDelta = 1;
            foreach (var element in value)
            {
                _pairCodec.WriteField(ref writer, innerFieldIdDelta, CodecFieldType, element);
                innerFieldIdDelta = 0;
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

            if (field.WireType != WireType.TagDelimited)
            {
                ThrowUnsupportedWireTypeException(field);
            }

            var placeholderReferenceId = ReferenceCodec.CreateRecordPlaceholder(reader.Session);
            int length = 0;
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
                        length = Int32Codec.ReadValue(ref reader, header);
                        if (length > 10240 && length > reader.Length)
                        {
                            ThrowInvalidSizeException(length);
                        }

                        break;
                    case 2:
                        result ??= CreateInstance(length, comparer, reader.Session, placeholderReferenceId);
                        var pair = _pairCodec.ReadValue(ref reader, header);
                        result.Add(pair.Key, pair.Value);
                        break;
                    default:
                        reader.ConsumeUnknownField(header);
                        break;
                }
            }

            result ??= CreateInstance(length, comparer, reader.Session, placeholderReferenceId);
            return result;
        }

        private Dictionary<TKey, TValue> CreateInstance(int length, IEqualityComparer<TKey> comparer, SerializerSession session, uint placeholderReferenceId)
        {
            var result = new Dictionary<TKey, TValue>(length, comparer);
            ReferenceCodec.RecordObject(session, result, placeholderReferenceId);
            return result;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowUnsupportedWireTypeException(Field field) => throw new UnsupportedWireTypeException(
            $"Only a {nameof(WireType)} value of {WireType.TagDelimited} is supported. {field}");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowInvalidSizeException(int length) => throw new IndexOutOfRangeException(
            $"Declared length of {typeof(Dictionary<TKey, TValue>)}, {length}, is greater than total length of input.");
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