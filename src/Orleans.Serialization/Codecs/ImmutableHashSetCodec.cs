using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for <see cref="ImmutableHashSet{T}"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    [RegisterSerializer]
    public sealed class ImmutableHashSetCodec<T> : GeneralizedReferenceTypeSurrogateCodec<ImmutableHashSet<T>, ImmutableHashSetSurrogate<T>>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ImmutableHashSetCodec{T}"/> class.
        /// </summary>
        /// <param name="surrogateSerializer">The surrogate serializer.</param>
        public ImmutableHashSetCodec(IValueSerializer<ImmutableHashSetSurrogate<T>> surrogateSerializer) : base(surrogateSerializer)
        {
        }

        /// <inheritdoc/>
        public override ImmutableHashSet<T> ConvertFromSurrogate(ref ImmutableHashSetSurrogate<T> surrogate)
            => surrogate.Values is null ? null : ImmutableHashSet.CreateRange(surrogate.KeyComparer, surrogate.Values);

        /// <inheritdoc/>
        public override void ConvertToSurrogate(ImmutableHashSet<T> value, ref ImmutableHashSetSurrogate<T> surrogate)
        {
            if (value != null)
            {
                surrogate.Values = new(value);
                surrogate.KeyComparer = value.KeyComparer != EqualityComparer<T>.Default ? value.KeyComparer : null;
            }
        }
    }

    /// <summary>
    /// Surrogate type used by <see cref="ImmutableHashSetCodec{T}"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    [GenerateSerializer]
    public struct ImmutableHashSetSurrogate<T>
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
        public IEqualityComparer<T> KeyComparer { get; set; }
    }

    /// <summary>
    /// Copier for <see cref="ImmutableHashSet{T}"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    [RegisterCopier]
    public sealed class ImmutableHashSetCopier<T> : IDeepCopier<ImmutableHashSet<T>>
    {
        private readonly IDeepCopier<T> _copier;

        public ImmutableHashSetCopier(IDeepCopier<T> copier) => _copier = copier;

        /// <inheritdoc/>
        public ImmutableHashSet<T> DeepCopy(ImmutableHashSet<T> input, CopyContext context)
        {
            if (context.TryGetCopy<ImmutableHashSet<T>>(input, out var result))
                return result;

            if (input.IsEmpty)
                return input;

            var items = new List<T>(input.Count);
            foreach (var item in input)
                items.Add(_copier.DeepCopy(item, context));

            return ImmutableHashSet.CreateRange(input.KeyComparer, items);
        }
    }
}
