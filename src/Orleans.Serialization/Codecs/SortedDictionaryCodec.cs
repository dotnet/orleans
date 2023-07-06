using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;
using System;
using System.Collections.Generic;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for <see cref="SortedDictionary{TKey, TValue}"/>.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    [RegisterSerializer]
    public sealed class SortedDictionaryCodec<TKey, TValue> : GeneralizedReferenceTypeSurrogateCodec<SortedDictionary<TKey, TValue>, SortedDictionarySurrogate<TKey, TValue>>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SortedDictionaryCodec{TKey, TValue}"/> class.
        /// </summary>
        /// <param name="surrogateSerializer">The surrogate serializer.</param>
        public SortedDictionaryCodec(IValueSerializer<SortedDictionarySurrogate<TKey, TValue>> surrogateSerializer) : base(surrogateSerializer)
        {
        }

        /// <inheritdoc />
        public override SortedDictionary<TKey, TValue> ConvertFromSurrogate(ref SortedDictionarySurrogate<TKey, TValue> surrogate)
        {
            var result = new SortedDictionary<TKey, TValue>(surrogate.Comparer);
            foreach (var kvp in surrogate.Values)
            {
                result.Add(kvp.Key, kvp.Value);
            }

            return result;
        }

        /// <inheritdoc />
        public override void ConvertToSurrogate(SortedDictionary<TKey, TValue> value, ref SortedDictionarySurrogate<TKey, TValue> surrogate)
        {
            surrogate.Values = new(value);
            surrogate.Comparer = value.Comparer == Comparer<TKey>.Default ? null : value.Comparer;
        }
    }

    /// <summary>
    /// Surrogate type for <see cref="SortedDictionaryCodec{TKey, TValue}"/>.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    [GenerateSerializer]
    public struct SortedDictionarySurrogate<TKey, TValue>
    {
        /// <summary>
        /// Gets or sets the values.
        /// </summary>
        /// <value>The values.</value>
        [Id(0)]
        public List<KeyValuePair<TKey, TValue>> Values;

        /// <summary>
        /// Gets or sets the comparer.
        /// </summary>
        /// <value>The comparer.</value>
        [Id(1)]
        public IComparer<TKey> Comparer;
    }

    /// <summary>
    /// Copier  for <see cref="SortedDictionary{TKey, TValue}"/>.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    [RegisterCopier]
    public sealed class SortedDictionaryCopier<TKey, TValue> : IDeepCopier<SortedDictionary<TKey, TValue>>, IBaseCopier<SortedDictionary<TKey, TValue>>
    {
        private readonly Type _fieldType = typeof(SortedDictionary<TKey, TValue>);
        private readonly IDeepCopier<TKey> _keyCopier;
        private readonly IDeepCopier<TValue> _valueCopier;

        /// <summary>
        /// Initializes a new instance of the <see cref="SortedDictionaryCopier{TKey, TValue}"/> class.
        /// </summary>
        /// <param name="keyCopier">The key copier.</param>
        /// <param name="valueCopier">The value copier.</param>
        public SortedDictionaryCopier(IDeepCopier<TKey> keyCopier, IDeepCopier<TValue> valueCopier)
        {
            _keyCopier = keyCopier;
            _valueCopier = valueCopier;
        }

        /// <inheritdoc />
        public SortedDictionary<TKey, TValue> DeepCopy(SortedDictionary<TKey, TValue> input, CopyContext context)
        {
            if (context.TryGetCopy<SortedDictionary<TKey, TValue>>(input, out var result))
            {
                return result;
            }

            if (input.GetType() as object != _fieldType as object)
            {
                return context.DeepCopy(input);
            }

            result = new SortedDictionary<TKey, TValue>(input.Comparer);
            context.RecordCopy(input, result);
            foreach (var pair in input)
            {
                result[_keyCopier.DeepCopy(pair.Key, context)] = _valueCopier.DeepCopy(pair.Value, context);
            }

            return result;
        }

        /// <inheritdoc />
        public void DeepCopy(SortedDictionary<TKey, TValue> input, SortedDictionary<TKey, TValue> output, CopyContext context)
        {
            foreach (var pair in input)
            {
                output[_keyCopier.DeepCopy(pair.Key, context)] = _valueCopier.DeepCopy(pair.Value, context);
            }
        }
    }
}
