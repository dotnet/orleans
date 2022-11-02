using Orleans.Serialization.Cloning;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.Serializers;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for <see cref="ImmutableList{T}"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    [RegisterSerializer]
    public sealed class ImmutableListCodec<T> : GeneralizedReferenceTypeSurrogateCodec<ImmutableList<T>, ImmutableListSurrogate<T>>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ImmutableListCodec{T}"/> class.
        /// </summary>
        /// <param name="surrogateSerializer">The surrogate serializer.</param>
        public ImmutableListCodec(IValueSerializer<ImmutableListSurrogate<T>> surrogateSerializer) : base(surrogateSerializer)
        {
        }

        /// <inheritdoc/>
        public override ImmutableList<T> ConvertFromSurrogate(ref ImmutableListSurrogate<T> surrogate) => ImmutableList.CreateRange(surrogate.Values);

        /// <inheritdoc/>
        public override void ConvertToSurrogate(ImmutableList<T> value, ref ImmutableListSurrogate<T> surrogate) => surrogate.Values = new(value);
    }

    /// <summary>
    /// Surrogate type used by <see cref="ImmutableListCodec{T}"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    [GenerateSerializer]
    public struct ImmutableListSurrogate<T>
    {
        /// <summary>
        /// Gets or sets the values.
        /// </summary>
        /// <value>The values.</value>
        [Id(0)]
        public List<T> Values;
    }

    /// <summary>
    /// Copier for <see cref="ImmutableList{T}"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    [RegisterCopier]
    public sealed class ImmutableListCopier<T> : IDeepCopier<ImmutableList<T>>, IOptionalDeepCopier
    {
        private readonly IDeepCopier<T> _copier;

        public ImmutableListCopier(IDeepCopier<T> copier) => _copier = OrleansGeneratedCodeHelper.GetOptionalCopier(copier);

        public bool IsShallowCopyable() => _copier is null;

        /// <inheritdoc/>
        public ImmutableList<T> DeepCopy(ImmutableList<T> input, CopyContext context)
        {
            if (context.TryGetCopy<ImmutableList<T>>(input, out var result))
                return result;

            if (input.IsEmpty || _copier is null)
                return input;

            // There is a possibility for infinite recursion here if any value in the input collection is able to take part in a cyclic reference.
            // Mitigate that by returning a shallow-copy in such a case.
            context.RecordCopy(input, input);

            var items = new List<T>(input.Count);
            foreach (var item in input)
                items.Add(_copier.DeepCopy(item, context));

            var res = ImmutableList.CreateRange(items);
            context.RecordCopy(input, res);
            return res;
        }
    }
}
