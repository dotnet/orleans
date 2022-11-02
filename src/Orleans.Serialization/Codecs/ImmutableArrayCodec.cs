using System.Collections.Immutable;
using System.Linq;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.Serializers;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for <see cref="ImmutableArray{T}"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    [RegisterSerializer]
    public sealed class ImmutableArrayCodec<T> : GeneralizedValueTypeSurrogateCodec<ImmutableArray<T>, ImmutableArraySurrogate<T>>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ImmutableArrayCodec{T}"/> class.
        /// </summary>
        /// <param name="surrogateSerializer">The surrogate serializer.</param>
        public ImmutableArrayCodec(IValueSerializer<ImmutableArraySurrogate<T>> surrogateSerializer) : base(surrogateSerializer)
        {
        }

        /// <inheritdoc/>
        public override ImmutableArray<T> ConvertFromSurrogate(ref ImmutableArraySurrogate<T> surrogate)
            => surrogate.Values is { } v ? ImmutableArray.Create(v) : default;

        /// <inheritdoc/>
        public override void ConvertToSurrogate(ImmutableArray<T> value, ref ImmutableArraySurrogate<T> surrogate)
            => surrogate.Values = value.IsDefault ? null : value.ToArray();
    }

    /// <summary>
    /// Surrogate type used by <see cref="ImmutableArrayCodec{T}"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    [GenerateSerializer]
    public struct ImmutableArraySurrogate<T>
    {
        /// <summary>
        /// Gets or sets the values.
        /// </summary>
        /// <value>The values.</value>
        [Id(0)]
        public T[] Values;
    }

    /// <summary>
    /// Copier for <see cref="ImmutableArray{T}"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    [RegisterCopier]
    public sealed class ImmutableArrayCopier<T> : IDeepCopier<ImmutableArray<T>>, IOptionalDeepCopier
    {
        private readonly IDeepCopier<T> _copier;

        public ImmutableArrayCopier(IDeepCopier<T> copier) => _copier = OrleansGeneratedCodeHelper.GetOptionalCopier(copier);

        public bool IsShallowCopyable() => _copier is null;

        object IDeepCopier.DeepCopy(object input, CopyContext context)
        {
            if (_copier is null)
                return input;

            var array = (ImmutableArray<T>)input;
            return array.IsDefaultOrEmpty ? input : DeepCopy(array, context);
        }

        /// <inheritdoc/>
        public ImmutableArray<T> DeepCopy(ImmutableArray<T> input, CopyContext context)
            => _copier is null || input.IsDefaultOrEmpty ? input : ImmutableArray.CreateRange(input, (i, s) => s._copier.DeepCopy(i, s.context), (_copier, context));
    }
}
