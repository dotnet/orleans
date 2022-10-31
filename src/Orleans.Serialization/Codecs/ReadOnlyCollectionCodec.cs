using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Surrogate type used by <see cref="ReadOnlyCollectionCodec{T}"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    [GenerateSerializer]
    public struct ReadOnlyCollectionSurrogate<T>
    {
        /// <summary>
        /// Gets or sets the values.
        /// </summary>
        /// <value>The values.</value>
        [Id(0)]
        public List<T> Values;
    }

    /// <summary>
    /// Serializer for <see cref="ReadOnlyCollection{T}"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    [RegisterSerializer]
    public sealed class ReadOnlyCollectionCodec<T> : GeneralizedReferenceTypeSurrogateCodec<ReadOnlyCollection<T>, ReadOnlyCollectionSurrogate<T>>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ReadOnlyCollectionCodec{T}"/> class.
        /// </summary>
        /// <param name="surrogateSerializer">The surrogate serializer.</param>
        public ReadOnlyCollectionCodec(IValueSerializer<ReadOnlyCollectionSurrogate<T>> surrogateSerializer) : base(surrogateSerializer)
        {
        }

        /// <inheritdoc/>
        public override ReadOnlyCollection<T> ConvertFromSurrogate(ref ReadOnlyCollectionSurrogate<T> surrogate) => new(surrogate.Values);

        /// <inheritdoc/>
        public override void ConvertToSurrogate(ReadOnlyCollection<T> value, ref ReadOnlyCollectionSurrogate<T> surrogate) => surrogate.Values = new(value);
    }

    /// <summary>
    /// Copier for <see cref="ReadOnlyCollection{T}"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    [RegisterCopier]
    public sealed class ReadOnlyCollectionCopier<T> : IDeepCopier<ReadOnlyCollection<T>>
    {
        private readonly IDeepCopier<T> _elementCopier;

        /// <summary>
        /// Initializes a new instance of the <see cref="ReadOnlyCollectionCopier{T}"/> class.
        /// </summary>
        /// <param name="elementCopier">The element copier.</param>
        public ReadOnlyCollectionCopier(IDeepCopier<T> elementCopier)
        {
            _elementCopier = elementCopier;
        }

        /// <inheritdoc />
        public ReadOnlyCollection<T> DeepCopy(ReadOnlyCollection<T> input, CopyContext context)
        {
            if (context.TryGetCopy<ReadOnlyCollection<T>>(input, out var result))
            {
                return result;
            }

            if (input.GetType() != typeof(ReadOnlyCollection<T>))
            {
                return context.DeepCopy(input);
            }

            // There is a possibility for infinite recursion here if any value in the input collection is able to take part in a cyclic reference.
            // Mitigate that by returning a shallow-copy in such a case.
            context.RecordCopy(input, input);

            var tempResult = new T[input.Count];
            for (var i = 0; i < tempResult.Length; i++)
            {
                tempResult[i] = _elementCopier.DeepCopy(input[i], context);
            }

            result = new ReadOnlyCollection<T>(tempResult);
            context.RecordCopy(input, result);
            return result;
        }
    }
}