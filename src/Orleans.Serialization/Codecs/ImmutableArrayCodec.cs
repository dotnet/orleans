using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;
using System.Collections.Generic;
using System.Collections.Immutable;

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
        public override ImmutableArray<T> ConvertFromSurrogate(ref ImmutableArraySurrogate<T> surrogate) => surrogate.Values switch
        {
            null => default,
            object => ImmutableArray.CreateRange(surrogate.Values)
        };

        /// <inheritdoc/>
        public override void ConvertToSurrogate(ImmutableArray<T> value, ref ImmutableArraySurrogate<T> surrogate)
        {
            if (value.IsDefault)
            {
                surrogate = default;
            }
            else
            {
                surrogate = new ImmutableArraySurrogate<T>
                {
                    Values = new List<T>(value)
                };
            }
        }
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
        [Id(1)]
        public List<T> Values { get; set; }
    }

    /// <summary>
    /// Copier for <see cref="ImmutableArray{T}"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    [RegisterCopier]
    public sealed class ImmutableArrayCopier<T> : IDeepCopier<ImmutableArray<T>>
    {
        /// <inheritdoc/>
        public ImmutableArray<T> DeepCopy(ImmutableArray<T> input, CopyContext context) => input;
    }
}
