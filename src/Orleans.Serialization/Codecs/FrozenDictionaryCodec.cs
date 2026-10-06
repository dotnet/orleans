#if NET8_0_OR_GREATER
using Orleans.Serialization.Cloning;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.Serializers;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for <see cref="FrozenDictionary{TKey, TValue}"/>.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    [RegisterSerializer]
    public sealed class FrozenDictionaryCodec<TKey, TValue> : GeneralizedReferenceTypeSurrogateCodec<FrozenDictionary<TKey, TValue>, FrozenDictionarySurrogate<TKey, TValue>> where TKey : notnull
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="FrozenDictionaryCodec{TKey, TValue}"/> class.
        /// </summary>
        /// <param name="surrogateSerializer">The surrogate serializer.</param>
        public FrozenDictionaryCodec(IValueSerializer<FrozenDictionarySurrogate<TKey, TValue>> surrogateSerializer) : base(surrogateSerializer)
        {
        }

        /// <inheritdoc/>
        public override FrozenDictionary<TKey, TValue> ConvertFromSurrogate(ref FrozenDictionarySurrogate<TKey, TValue> surrogate)
            => surrogate.Values.ToFrozenDictionary(surrogate.KeyComparer);

        /// <inheritdoc/>
        public override void ConvertToSurrogate(FrozenDictionary<TKey, TValue> value, ref FrozenDictionarySurrogate<TKey, TValue> surrogate)
        {
            surrogate.Values = new(value);
            surrogate.KeyComparer = value.Comparer != EqualityComparer<TKey>.Default ? value.Comparer : null;
        }
    }

    /// <summary>
    /// Surrogate type used by <see cref="FrozenDictionaryCodec{TKey, TValue}"/>.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    [GenerateSerializer]
    public struct FrozenDictionarySurrogate<TKey, TValue> where TKey : notnull
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
        public IEqualityComparer<TKey>? KeyComparer;
    }

    /// <summary>
    /// Copier for <see cref="FrozenDictionary{TKey, TValue}"/>.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    [RegisterCopier]
    public sealed class FrozenDictionaryCopier<TKey, TValue> : IDeepCopier<FrozenDictionary<TKey, TValue>>, IOptionalDeepCopier, IDerivedTypeCopier where TKey : notnull
    {
        private int _shallowCopyable;
        private readonly IDeepCopier<TKey> _keyCopier;
        private readonly IDeepCopier<TValue> _valueCopier;

        /// <summary>
        /// Initializes a new instance of the <see cref="FrozenDictionaryCopier{TKey, TValue}"/> class.
        /// </summary>
        /// <param name="keyCopier">The key copier.</param>
        /// <param name="valueCopier">The value copier.</param>
        public FrozenDictionaryCopier(IDeepCopier<TKey> keyCopier, IDeepCopier<TValue> valueCopier)
        {
            _keyCopier = OrleansGeneratedCodeHelper.UnwrapService(this, keyCopier);
            _valueCopier = OrleansGeneratedCodeHelper.UnwrapService(this, valueCopier);
        }

        /// <inheritdoc />
        public bool IsShallowCopyable() => OrleansGeneratedCodeHelper.IsShallowCopyable(
            ref _shallowCopyable, this, static self => OrleansGeneratedCodeHelper.IsShallowCopyable(self._keyCopier) && OrleansGeneratedCodeHelper.IsShallowCopyable(self._valueCopier));

        /// <inheritdoc/>
        [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(input))]
        public FrozenDictionary<TKey, TValue>? DeepCopy(FrozenDictionary<TKey, TValue>? input, CopyContext context)
        {
            ArgumentNullExceptionPolyfill.ThrowIfNull(context);

            if (context.TryGetCopy<FrozenDictionary<TKey, TValue>>(input, out var result))
                return result!;

            System.Diagnostics.Debug.Assert(input is not null);
            if (input.Count == 0 || IsShallowCopyable())
                return input;

            // There is a possibility for infinite recursion here if any value in the input collection is able to take part in a cyclic reference.
            // Mitigate that by returning a shallow-copy in such a case.
            context.RecordCopy(input, input);

            var shallowKeys = OrleansGeneratedCodeHelper.IsShallowCopyable(_keyCopier);
            var shallowValues = OrleansGeneratedCodeHelper.IsShallowCopyable(_valueCopier);
            var items = new List<KeyValuePair<TKey, TValue>>(input.Count);
            foreach (var item in input)
                items.Add(new(shallowKeys ? item.Key : _keyCopier.DeepCopy(item.Key, context),
                    shallowValues ? item.Value : _valueCopier.DeepCopy(item.Value, context)!));

            var res = items.ToFrozenDictionary(input.Comparer);
            context.RecordCopy(input, res);
            return res;
        }
    }
}
#endif
