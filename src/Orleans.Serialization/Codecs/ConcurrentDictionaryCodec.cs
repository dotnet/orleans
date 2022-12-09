using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for <see cref="ConcurrentDictionary{TKey, TValue}"/>.
    /// </summary>
    /// <typeparam name="TKey">The type of the t key.</typeparam>
    /// <typeparam name="TValue">The type of the t value.</typeparam>
    [RegisterSerializer]
    public sealed class ConcurrentDictionaryCodec<TKey, TValue> : GeneralizedReferenceTypeSurrogateCodec<ConcurrentDictionary<TKey, TValue>, ConcurrentDictionarySurrogate<TKey, TValue>>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ConcurrentDictionaryCodec{TKey, TValue}"/> class.
        /// </summary>
        /// <param name="surrogateSerializer">The surrogate serializer.</param>
        public ConcurrentDictionaryCodec(IValueSerializer<ConcurrentDictionarySurrogate<TKey, TValue>> surrogateSerializer) : base(surrogateSerializer)
        {
        }

        /// <inheritdoc/>
        public override ConcurrentDictionary<TKey, TValue> ConvertFromSurrogate(ref ConcurrentDictionarySurrogate<TKey, TValue> surrogate)
            => new(surrogate.Values, surrogate.Values.Comparer);

        /// <inheritdoc/>
        public override void ConvertToSurrogate(ConcurrentDictionary<TKey, TValue> value, ref ConcurrentDictionarySurrogate<TKey, TValue> surrogate)
#if NET6_0_OR_GREATER
            => surrogate.Values = new(value, value.Comparer);
#else
            => surrogate.Values = new(value);
#endif
    }

    /// <summary>
    /// Surrogate type used by <see cref="ConcurrentDictionaryCodec{TKey, TValue}"/>.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    [GenerateSerializer]
    public struct ConcurrentDictionarySurrogate<TKey, TValue>
    {
        /// <summary>
        /// Gets or sets the values.
        /// </summary>
        /// <value>The values.</value>
        [Id(0)]
        public Dictionary<TKey, TValue> Values;
    }

    /// <summary>
    /// Copier for <see cref="ConcurrentDictionary{TKey, TValue}"/>.
    /// </summary>
    /// <typeparam name="TKey">The type of the t key.</typeparam>
    /// <typeparam name="TValue">The type of the t value.</typeparam>
    [RegisterCopier]
    public sealed class ConcurrentDictionaryCopier<TKey, TValue> : IDeepCopier<ConcurrentDictionary<TKey, TValue>>, IBaseCopier<ConcurrentDictionary<TKey, TValue>>
    {
        private readonly IDeepCopier<TKey> _keyCopier;
        private readonly IDeepCopier<TValue> _valueCopier;

        /// <summary>
        /// Initializes a new instance of the <see cref="ConcurrentDictionaryCopier{TKey, TValue}"/> class.
        /// </summary>
        /// <param name="keyCopier">The key copier.</param>
        /// <param name="valueCopier">The value copier.</param>
        public ConcurrentDictionaryCopier(IDeepCopier<TKey> keyCopier, IDeepCopier<TValue> valueCopier)
        {
            _keyCopier = keyCopier;
            _valueCopier = valueCopier;
        }

        /// <inheritdoc/>
        public ConcurrentDictionary<TKey, TValue> DeepCopy(ConcurrentDictionary<TKey, TValue> input, CopyContext context)
        {
            if (context.TryGetCopy<ConcurrentDictionary<TKey, TValue>>(input, out var result))
            {
                return result;
            }

            if (input.GetType() != typeof(ConcurrentDictionary<TKey, TValue>))
            {
                return context.DeepCopy(input);
            }

#if NET6_0_OR_GREATER
            result = new(input.Comparer);
#else
            result = new();
#endif
            context.RecordCopy(input, result);
            foreach (var pair in input)
            {
                result[_keyCopier.DeepCopy(pair.Key, context)] = _valueCopier.DeepCopy(pair.Value, context);
            }

            return result;
        }

        /// <inheritdoc/>
        public void DeepCopy(ConcurrentDictionary<TKey, TValue> input, ConcurrentDictionary<TKey, TValue> output, CopyContext context)
        {
            foreach (var pair in input)
            {
                output[_keyCopier.DeepCopy(pair.Key, context)] = _valueCopier.DeepCopy(pair.Value, context);
            }
        }
    }
}
