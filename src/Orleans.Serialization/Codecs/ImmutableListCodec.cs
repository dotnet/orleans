using Orleans.Serialization.Cloning;
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
        public override ImmutableList<T> ConvertFromSurrogate(ref ImmutableListSurrogate<T> surrogate) => surrogate.Values switch
        {
            null => default,
            object => ImmutableList.CreateRange(surrogate.Values)
        };

        /// <inheritdoc/>
        public override void ConvertToSurrogate(ImmutableList<T> value, ref ImmutableListSurrogate<T> surrogate)
        {
            if (value is null)
            {
                surrogate = default;
            }
            else
            {
                surrogate = new ImmutableListSurrogate<T>
                {
                    Values = new List<T>(value)
                };
            }
        }
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
        [Id(1)]
        public List<T> Values { get; set; }
    }

    /// <summary>
    /// Copier for <see cref="ImmutableList{T}"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    [RegisterCopier]
    public sealed class ImmutableListCopier<T> : IDeepCopier<ImmutableList<T>>
    {
        /// <inheritdoc/>
        public ImmutableList<T> DeepCopy(ImmutableList<T> input, CopyContext _) => input;
    }
}
