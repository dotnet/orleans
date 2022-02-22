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
        {
            if (surrogate.Values is null)
            {
                return null;
            }
            else
            {
                if (surrogate.KeyComparer is object)
                {
                    return ImmutableSortedSet.CreateRange(surrogate.KeyComparer, surrogate.Values);
                }
                else
                {
                    return ImmutableSortedSet.CreateRange(surrogate.Values);
                }
            }
        }

        /// <inheritdoc/>
        public override void ConvertToSurrogate(ImmutableSortedSet<T> value, ref ImmutableSortedSetSurrogate<T> surrogate)
        {
            if (value is null)
            {
                surrogate = default;
                return;
            }
            else
            {
                surrogate = new ImmutableSortedSetSurrogate<T>
                {
                    Values = new List<T>(value)
                };

                if (!ReferenceEquals(value.KeyComparer, Comparer<T>.Default))
                {
                    surrogate.KeyComparer = value.KeyComparer;
                }
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
        /// <inheritdoc/>
        public ImmutableSortedSet<T> DeepCopy(ImmutableSortedSet<T> input, CopyContext _) => input;
    }
}
