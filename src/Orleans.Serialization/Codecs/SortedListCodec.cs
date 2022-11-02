using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;
using System.Collections.Generic;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for <see cref="SortedList{TKey, TValue}"/>.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    [RegisterSerializer]
    public sealed class SortedListCodec<TKey, TValue> : GeneralizedReferenceTypeSurrogateCodec<SortedList<TKey, TValue>, SortedListSurrogate<TKey, TValue>>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SortedListCodec{TKey, TValue}"/> class.
        /// </summary>
        /// <param name="surrogateSerializer">The surrogate serializer.</param>
        public SortedListCodec(IValueSerializer<SortedListSurrogate<TKey, TValue>> surrogateSerializer) : base(surrogateSerializer)
        {
        }

        /// <inheritdoc />
        public override SortedList<TKey, TValue> ConvertFromSurrogate(ref SortedListSurrogate<TKey, TValue> surrogate)
        {
            if (surrogate.Values is null)
            {
                return null;
            }
            else
            {
                var result = new SortedList<TKey, TValue>(surrogate.Comparer);
                foreach (var kvp in surrogate.Values)
                {
                    result.Add(kvp.Key, kvp.Value);
                }

                return result;
            }
        }

        /// <inheritdoc />
        public override void ConvertToSurrogate(SortedList<TKey, TValue> value, ref SortedListSurrogate<TKey, TValue> surrogate)
        {
            surrogate.Values = new(value);
            surrogate.Comparer = value.Comparer == Comparer<TKey>.Default ? null : value.Comparer;
        }
    }

    /// <summary>
    /// Surrogate type for <see cref="SortedListCodec{TKey, TValue}"/>.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    [GenerateSerializer]
    public struct SortedListSurrogate<TKey, TValue>
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
    /// Copier for <see cref="SortedList{TKey, TValue}"/>.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    [RegisterCopier]
    public sealed class SortedListCopier<TKey, TValue> : IDeepCopier<SortedList<TKey, TValue>>, IBaseCopier<SortedList<TKey, TValue>>
    {
        private readonly IDeepCopier<TKey> _keyCopier;
        private readonly IDeepCopier<TValue> _valueCopier;

        /// <summary>
        /// Initializes a new instance of the <see cref="SortedListCopier{TKey, TValue}"/> class.
        /// </summary>
        /// <param name="keyCopier">The key copier.</param>
        /// <param name="valueCopier">The value copier.</param>
        public SortedListCopier(IDeepCopier<TKey> keyCopier, IDeepCopier<TValue> valueCopier)
        {
            _keyCopier = keyCopier;
            _valueCopier = valueCopier;
        }

        /// <inheritdoc />
        public SortedList<TKey, TValue> DeepCopy(SortedList<TKey, TValue> input, CopyContext context)
        {
            if (context.TryGetCopy<SortedList<TKey, TValue>>(input, out var result))
            {
                return result;
            }

            if (input.GetType() != typeof(SortedList<TKey, TValue>))
            {
                return context.DeepCopy(input);
            }

            result = new SortedList<TKey, TValue>(input.Comparer);
            context.RecordCopy(input, result);
            foreach (var pair in input)
            {
                result[_keyCopier.DeepCopy(pair.Key, context)] = _valueCopier.DeepCopy(pair.Value, context);
            }

            return result;
        }

        /// <inheritdoc />
        public void DeepCopy(SortedList<TKey, TValue> input, SortedList<TKey, TValue> output, CopyContext context)
        {
            foreach (var pair in input)
            {
                output[_keyCopier.DeepCopy(pair.Key, context)] = _valueCopier.DeepCopy(pair.Value, context);
            }
        }
    }
}
