using Orleans.Serialization.Cloning;
using Orleans.Serialization.GeneratedCodeHelpers;
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
            => ImmutableSortedSet.CreateRange(surrogate.KeyComparer, surrogate.Values);

        /// <inheritdoc/>
        public override void ConvertToSurrogate(ImmutableSortedSet<T> value, ref ImmutableSortedSetSurrogate<T> surrogate)
        {
            surrogate.Values = new(value);
            surrogate.KeyComparer = value.KeyComparer != Comparer<T>.Default ? value.KeyComparer : null;
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
        [Id(0)]
        public List<T> Values;

        /// <summary>
        /// Gets or sets the key comparer.
        /// </summary>
        /// <value>The key comparer.</value>
        [Id(1)]
        public IComparer<T> KeyComparer;
    }

    /// <summary>
    /// Copier for <see cref="ImmutableSortedSet{T}"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    [RegisterCopier]
    public sealed class ImmutableSortedSetCopier<T> : IDeepCopier<ImmutableSortedSet<T>>, IOptionalDeepCopier
    {
        private readonly IDeepCopier<T> _copier;

        public ImmutableSortedSetCopier(IDeepCopier<T> copier) => _copier = OrleansGeneratedCodeHelper.GetOptionalCopier(copier);

        public bool IsShallowCopyable() => _copier is null;

        /// <inheritdoc/>
        public ImmutableSortedSet<T> DeepCopy(ImmutableSortedSet<T> input, CopyContext context)
        {
            if (context.TryGetCopy<ImmutableSortedSet<T>>(input, out var result))
                return result;

            if (input.IsEmpty || _copier is null)
                return input;

            // There is a possibility for infinite recursion here if any value in the input collection is able to take part in a cyclic reference.
            // Mitigate that by returning a shallow-copy in such a case.
            context.RecordCopy(input, input);

            var items = new List<T>(input.Count);
            foreach (var item in input)
                items.Add(_copier.DeepCopy(item, context));

            var res = ImmutableSortedSet.CreateRange(input.KeyComparer, items);
            context.RecordCopy(input, res);
            return res;
        }
    }
}
