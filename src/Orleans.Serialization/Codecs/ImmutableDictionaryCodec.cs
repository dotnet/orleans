using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for <see cref="ImmutableDictionary{TKey, TValue}"/>.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    [RegisterSerializer]
    public sealed class ImmutableDictionaryCodec<TKey, TValue> : GeneralizedReferenceTypeSurrogateCodec<ImmutableDictionary<TKey, TValue>, ImmutableDictionarySurrogate<TKey, TValue>>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ImmutableDictionaryCodec{TKey, TValue}"/> class.
        /// </summary>
        /// <param name="surrogateSerializer">The surrogate serializer.</param>
        public ImmutableDictionaryCodec(IValueSerializer<ImmutableDictionarySurrogate<TKey, TValue>> surrogateSerializer) : base(surrogateSerializer)
        {
        }

        /// <inheritdoc/>
        public override ImmutableDictionary<TKey, TValue> ConvertFromSurrogate(ref ImmutableDictionarySurrogate<TKey, TValue> surrogate)
            => surrogate.Values is { } v ? ImmutableDictionary.CreateRange(v.Comparer, v) : null;

        /// <inheritdoc/>
        public override void ConvertToSurrogate(ImmutableDictionary<TKey, TValue> value, ref ImmutableDictionarySurrogate<TKey, TValue> surrogate)
            => surrogate.Values = value is null ? null : new(value, value.KeyComparer);
    }

    /// <summary>
    /// Surrogate type used by <see cref="ImmutableDictionaryCodec{TKey, TValue}"/>.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    [GenerateSerializer]
    public struct ImmutableDictionarySurrogate<TKey, TValue>
    {
        /// <summary>
        /// Gets or sets the values.
        /// </summary>
        /// <value>The values.</value>
        [Id(1)]
        public Dictionary<TKey, TValue> Values { get; set; }
    }

    /// <summary>
    /// Copier for <see cref="ImmutableDictionary{TKey, TValue}"/>.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    [RegisterCopier]
    public sealed class ImmutableDictionaryCopier<TKey, TValue> : IDeepCopier<ImmutableDictionary<TKey, TValue>>
    {
        private readonly IDeepCopier<TKey> _keyCopier;
        private readonly IDeepCopier<TValue> _valueCopier;

        public ImmutableDictionaryCopier(IDeepCopier<TKey> keyCopier, IDeepCopier<TValue> valueCopier)
        {
            _keyCopier = keyCopier;
            _valueCopier = valueCopier;
        }

        /// <inheritdoc/>
        public ImmutableDictionary<TKey, TValue> DeepCopy(ImmutableDictionary<TKey, TValue> input, CopyContext context)
        {
            if (context.TryGetCopy<ImmutableDictionary<TKey, TValue>>(input, out var result))
                return result;

            if (input.IsEmpty)
                return input;

            var items = new List<KeyValuePair<TKey, TValue>>(input.Count);
            foreach (var item in input)
                items.Add(new(_keyCopier.DeepCopy(item.Key, context), _valueCopier.DeepCopy(item.Value, context)));

            return ImmutableDictionary.CreateRange(input.KeyComparer, input.ValueComparer, items);
        }
    }
}
