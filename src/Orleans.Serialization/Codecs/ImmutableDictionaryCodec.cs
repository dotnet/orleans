using Orleans.Serialization.Cloning;
using Orleans.Serialization.GeneratedCodeHelpers;
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
            => ImmutableDictionary.CreateRange(surrogate.Values.Comparer, surrogate.Values);

        /// <inheritdoc/>
        public override void ConvertToSurrogate(ImmutableDictionary<TKey, TValue> value, ref ImmutableDictionarySurrogate<TKey, TValue> surrogate)
            => surrogate.Values = new(value, value.KeyComparer);
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
        [Id(0)]
        public Dictionary<TKey, TValue> Values;
    }

    /// <summary>
    /// Copier for <see cref="ImmutableDictionary{TKey, TValue}"/>.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    [RegisterCopier]
    public sealed class ImmutableDictionaryCopier<TKey, TValue> : IDeepCopier<ImmutableDictionary<TKey, TValue>>, IOptionalDeepCopier
    {
        private readonly IDeepCopier<TKey> _keyCopier;
        private readonly IDeepCopier<TValue> _valueCopier;

        public ImmutableDictionaryCopier(IDeepCopier<TKey> keyCopier, IDeepCopier<TValue> valueCopier)
        {
            _keyCopier = OrleansGeneratedCodeHelper.GetOptionalCopier(keyCopier);
            _valueCopier = OrleansGeneratedCodeHelper.GetOptionalCopier(valueCopier);
        }

        public bool IsShallowCopyable() => _keyCopier is null && _valueCopier is null;

        /// <inheritdoc/>
        public ImmutableDictionary<TKey, TValue> DeepCopy(ImmutableDictionary<TKey, TValue> input, CopyContext context)
        {
            if (context.TryGetCopy<ImmutableDictionary<TKey, TValue>>(input, out var result))
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

            var res = ImmutableDictionary.CreateRange(input.KeyComparer, input.ValueComparer, items);
            context.RecordCopy(input, res);
            return res;
        }
    }
}
