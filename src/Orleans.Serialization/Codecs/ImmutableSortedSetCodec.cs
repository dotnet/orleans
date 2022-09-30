using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for <see cref="ImmutableSortedSet{T}"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    [RegisterSerializer]
    public sealed class ImmutableSortedSetCodec<T> : GeneralizedReferenceTypeSurrogateCodec<ImmutableSortedSet<T>, ImmutableSortedSetSurrogate<T>>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ImmutableSortedSetCodec{T}"/> class.
        /// </summary>
        /// <param name="surrogateSerializer">The surrogate serializer.</param>
        public ImmutableSortedSetCodec(IValueSerializer<ImmutableSortedSetSurrogate<T>> surrogateSerializer) : base(surrogateSerializer)
        {
        }

        /// <inheritdoc/>
        public override ImmutableSortedSet<T> ConvertFromSurrogate(ref ImmutableSortedSetSurrogate<T> surrogate)
            => surrogate.Values is null ? null : ImmutableSortedSet.CreateRange(surrogate.KeyComparer, surrogate.Values);

        /// <inheritdoc/>
        public override void ConvertToSurrogate(ImmutableSortedSet<T> value, ref ImmutableSortedSetSurrogate<T> surrogate)
        {
            if (value != null)
            {
                surrogate.Values = new(value);
                surrogate.KeyComparer = value.KeyComparer != EqualityComparer<T>.Default ? value.KeyComparer : null;
            }
        }
    }

    /// <summary>
    /// Surrogate type used by <see cref="ImmutableSortedSetCodec{T}"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    [GenerateSerializer]
    public struct ImmutableSortedSetSurrogate<T>
    {
        /// <summary>
        /// Gets or sets the values.
        /// </summary>
        /// <value>The values.</value>
        [Id(1)]
        public List<T> Values { get; set; }

        /// <summary>
        /// Gets or sets the key comparer.
        /// </summary>
        /// <value>The key comparer.</value>
        [Id(2)]
        public IComparer<T> KeyComparer { get; set; }
    }

    /// <summary>
    /// Copier for <see cref="ImmutableSortedSet{T}"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    [RegisterCopier]
    public sealed class ImmutableSortedSetCopier<T> : IDeepCopier<ImmutableSortedSet<T>>
    {
        private readonly IDeepCopier<T> _copier;

        public ImmutableSortedSetCopier(IDeepCopier<T> copier) => _copier = copier;

        /// <inheritdoc/>
        public ImmutableSortedSet<T> DeepCopy(ImmutableSortedSet<T> input, CopyContext context)
        {
            if (context.TryGetCopy<ImmutableSortedSet<T>>(input, out var result))
                return result;

            if (input.IsEmpty)
                return input;

            var items = new List<T>(input.Count);
            foreach (var item in input)
                items.Add(_copier.DeepCopy(item, context));

            return ImmutableSortedSet.CreateRange(input.KeyComparer, items);
        }
    }
}
