using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for <see cref="ImmutableSortedDictionary{TKey, TValue}"/>.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    [RegisterSerializer]
    public sealed class ImmutableSortedDictionaryCodec<TKey, TValue> : GeneralizedReferenceTypeSurrogateCodec<ImmutableSortedDictionary<TKey, TValue>, ImmutableSortedDictionarySurrogate<TKey, TValue>>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ImmutableSortedDictionaryCodec{TKey, TValue}"/> class.
        /// </summary>
        /// <param name="surrogateSerializer">The surrogate serializer.</param>
        public ImmutableSortedDictionaryCodec(IValueSerializer<ImmutableSortedDictionarySurrogate<TKey, TValue>> surrogateSerializer) : base(surrogateSerializer)
        {
        }

        /// <inheritdoc/>
        public override ImmutableSortedDictionary<TKey, TValue> ConvertFromSurrogate(ref ImmutableSortedDictionarySurrogate<TKey, TValue> surrogate)
            => surrogate.Values is null ? null : ImmutableSortedDictionary.CreateRange(surrogate.KeyComparer, surrogate.ValueComparer, surrogate.Values);

        /// <inheritdoc/>
        public override void ConvertToSurrogate(ImmutableSortedDictionary<TKey, TValue> value, ref ImmutableSortedDictionarySurrogate<TKey, TValue> surrogate)
        {
            if (value != null)
            {
                surrogate.Values = new(value);
                surrogate.KeyComparer = value.KeyComparer != Comparer<TKey>.Default ? value.KeyComparer : null;
                surrogate.ValueComparer = value.ValueComparer != EqualityComparer<TKey>.Default ? value.ValueComparer : null;
            }
        }
    }

    /// <summary>
    /// Surrogate type used by <see cref="ImmutableSortedDictionaryCodec{TKey, TValue}"/>.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    [GenerateSerializer]
    public struct ImmutableSortedDictionarySurrogate<TKey, TValue>
    {
        /// <summary>
        /// Gets or sets the values.
        /// </summary>
        /// <value>The values.</value>
        [Id(1)]
        public List<KeyValuePair<TKey, TValue>> Values { get; set; }

        /// <summary>
        /// Gets or sets the key comparer.
        /// </summary>
        /// <value>The key comparer.</value>
        [Id(2)]
        public IComparer<TKey> KeyComparer { get; set; }
        [Id(3)]
        public IEqualityComparer<TValue> ValueComparer { get; set; }
    }

    /// <summary>
    /// Copier for <see cref="ImmutableSortedDictionary{TKey, TValue}"/>.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    [RegisterCopier]
    public sealed class ImmutableSortedDictionaryCopier<TKey, TValue> : IDeepCopier<ImmutableSortedDictionary<TKey, TValue>>
    {
        private readonly IDeepCopier<TKey> _keyCopier;
        private readonly IDeepCopier<TValue> _valueCopier;

        public ImmutableSortedDictionaryCopier(IDeepCopier<TKey> keyCopier, IDeepCopier<TValue> valueCopier)
        {
            _keyCopier = keyCopier;
            _valueCopier = valueCopier;
        }

        /// <inheritdoc/>
        public ImmutableSortedDictionary<TKey, TValue> DeepCopy(ImmutableSortedDictionary<TKey, TValue> input, CopyContext context)
        {
            if (context.TryGetCopy<ImmutableSortedDictionary<TKey, TValue>>(input, out var result))
                return result;

            if (input.IsEmpty)
                return input;

            var items = new List<KeyValuePair<TKey, TValue>>(input.Count);
            foreach (var item in input)
                items.Add(new(_keyCopier.DeepCopy(item.Key, context), _valueCopier.DeepCopy(item.Value, context)));

            return ImmutableSortedDictionary.CreateRange(input.KeyComparer, input.ValueComparer, items);
        }
    }
}
