#if NET8_0_OR_GREATER
using Orleans.Serialization.Cloning;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.Serializers;
using System.Collections.Frozen;
using System.Collections.Generic;

#nullable disable
namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for <see cref="FrozenSet{T}"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    [RegisterSerializer]
    public sealed class FrozenSetCodec<T> : GeneralizedReferenceTypeSurrogateCodec<FrozenSet<T>, FrozenSetSurrogate<T>>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="FrozenSetCodec{T}"/> class.
        /// </summary>
        /// <param name="surrogateSerializer">The surrogate serializer.</param>
        public FrozenSetCodec(IValueSerializer<FrozenSetSurrogate<T>> surrogateSerializer) : base(surrogateSerializer)
        {
        }

        /// <inheritdoc/>
        public override FrozenSet<T> ConvertFromSurrogate(ref FrozenSetSurrogate<T> surrogate)
            => surrogate.Values.ToFrozenSet(surrogate.KeyComparer);

        /// <inheritdoc/>
        public override void ConvertToSurrogate(FrozenSet<T> value, ref FrozenSetSurrogate<T> surrogate)
        {
            surrogate.Values = new(value);
            surrogate.KeyComparer = value.Comparer != EqualityComparer<T>.Default ? value.Comparer : null;
        }
    }

    /// <summary>
    /// Surrogate type used by <see cref="FrozenSetCodec{T}"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    [GenerateSerializer]
    public struct FrozenSetSurrogate<T>
    {
        /// <summary>
        /// Gets or sets the values.
        /// </summary>
        /// <value>The values.</value>
        [Id(0)]
        public List<T> Values;

        /// <summary>
        /// Gets or sets the key comparer.
        /// </summary>
        /// <value>The key comparer.</value>
        [Id(1)]
        public IEqualityComparer<T> KeyComparer;
    }

    /// <summary>
    /// Copier for <see cref="FrozenSet{T}"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    [RegisterCopier]
    public sealed class FrozenSetCopier<T> : IDeepCopier<FrozenSet<T>>, IOptionalDeepCopier, IDerivedTypeCopier
    {
        private readonly IDeepCopier<T> _copier;

        public FrozenSetCopier(IDeepCopier<T> copier) => _copier = OrleansGeneratedCodeHelper.GetOptionalCopier(copier);

        public bool IsShallowCopyable() => _copier is null;

        /// <inheritdoc/>
        public FrozenSet<T> DeepCopy(FrozenSet<T> input, CopyContext context)
        {
            if (context.TryGetCopy<FrozenSet<T>>(input, out var result))
                return result;

            if (input.Count == 0 || _copier is null)
                return input;

            // There is a possibility for infinite recursion here if any value in the input collection is able to take part in a cyclic reference.
            // Mitigate that by returning a shallow-copy in such a case.
            context.RecordCopy(input, input);

            var items = new List<T>(input.Count);
            foreach (var item in input)
                items.Add(_copier.DeepCopy(item, context));

            var res = items.ToFrozenSet(input.Comparer);
            context.RecordCopy(input, res);
            return res;
        }
    }
}
#endif
