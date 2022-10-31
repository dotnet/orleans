using Orleans.Serialization.Cloning;
using Orleans.Serialization.GeneratedCodeHelpers;
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
            => ImmutableSortedDictionary.CreateRange(surrogate.KeyComparer, surrogate.ValueComparer, surrogate.Values);

        /// <inheritdoc/>
        public override void ConvertToSurrogate(ImmutableSortedDictionary<TKey, TValue> value, ref ImmutableSortedDictionarySurrogate<TKey, TValue> surrogate)
        {
            surrogate.Values = new(value);
            surrogate.KeyComparer = value.KeyComparer != Comparer<TKey>.Default ? value.KeyComparer : null;
            surrogate.ValueComparer = value.ValueComparer != EqualityComparer<TKey>.Default ? value.ValueComparer : null;
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
        [Id(0)]
        public List<KeyValuePair<TKey, TValue>> Values;

        /// <summary>
        /// Gets or sets the key comparer.
        /// </summary>
        /// <value>The key comparer.</value>
        [Id(1)]
        public IComparer<TKey> KeyComparer;
        [Id(2)]
        public IEqualityComparer<TValue> ValueComparer;
    }

    /// <summary>
    /// Copier for <see cref="ImmutableSortedDictionary{TKey, TValue}"/>.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    [RegisterCopier]
    public sealed class ImmutableSortedDictionaryCopier<TKey, TValue> : IDeepCopier<ImmutableSortedDictionary<TKey, TValue>>, IOptionalDeepCopier
    {
        private readonly IDeepCopier<TKey> _keyCopier;
        private readonly IDeepCopier<TValue> _valueCopier;

        public ImmutableSortedDictionaryCopier(IDeepCopier<TKey> keyCopier, IDeepCopier<TValue> valueCopier)
        {
            _keyCopier = OrleansGeneratedCodeHelper.GetOptionalCopier(keyCopier);
            _valueCopier = OrleansGeneratedCodeHelper.GetOptionalCopier(valueCopier);
        }

        public bool IsShallowCopyable() => _keyCopier is null && _valueCopier is null;

        /// <inheritdoc/>
        public ImmutableSortedDictionary<TKey, TValue> DeepCopy(ImmutableSortedDictionary<TKey, TValue> input, CopyContext context)
        {
            if (context.TryGetCopy<ImmutableSortedDictionary<TKey, TValue>>(input, out var result))
                return result;

            if (input.IsEmpty || _keyCopier is null && _valueCopier is null)
                return input;

            // There is a possibility for infinite recursion here if any value in the input collection is able to take part in a cyclic reference.
            // Mitigate that by returning a shallow-copy in such a case.
            context.RecordCopy(input, input);

            var items = new List<KeyValuePair<TKey, TValue>>(input.Count);
            foreach (var item in input)
                items.Add(new(_keyCopier is null ? item.Key : _keyCopier.DeepCopy(item.Key, context),
                    _valueCopier is null ? item.Value : _valueCopier.DeepCopy(item.Value, context)));

            var res = ImmutableSortedDictionary.CreateRange(input.KeyComparer, input.ValueComparer, items);
            context.RecordCopy(input, res);
            return res;
        }
    }
}
