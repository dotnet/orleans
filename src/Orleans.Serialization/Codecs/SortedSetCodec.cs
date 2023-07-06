using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;
using System;
using System.Collections.Generic;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for <see cref="SortedSet{T}"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    [RegisterSerializer]
    public sealed class SortedSetCodec<T> : GeneralizedReferenceTypeSurrogateCodec<SortedSet<T>, SortedSetSurrogate<T>>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SortedSetCodec{T}"/> class.
        /// </summary>
        /// <param name="surrogateSerializer">The surrogate serializer.</param>
        public SortedSetCodec(IValueSerializer<SortedSetSurrogate<T>> surrogateSerializer) : base(surrogateSerializer)
        {
        }

        /// <inheritdoc />
        public override SortedSet<T> ConvertFromSurrogate(ref SortedSetSurrogate<T> surrogate) => new(surrogate.Values, surrogate.Comparer);

        /// <inheritdoc />
        public override void ConvertToSurrogate(SortedSet<T> value, ref SortedSetSurrogate<T> surrogate)
        {
            surrogate.Values = new(value);
            surrogate.Comparer = value.Comparer == Comparer<T>.Default ? null : value.Comparer;
        }
    }

    /// <summary>
    /// Surrogate type for <see cref="SortedSetCodec{T}"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    [GenerateSerializer]
    public struct SortedSetSurrogate<T>
    {
        /// <summary>
        /// Gets or sets the values.
        /// </summary>
        /// <value>The values.</value>
        [Id(0)]
        public List<T> Values;

        /// <summary>
        /// Gets or sets the comparer.
        /// </summary>
        /// <value>The comparer.</value>
        [Id(1)]
        public IComparer<T> Comparer;
    }

    /// <summary>
    /// Copier for <see cref="SortedSet{T}"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    [RegisterCopier]
    public sealed class SortedSetCopier<T> : IDeepCopier<SortedSet<T>>, IBaseCopier<SortedSet<T>>
    {
        private readonly Type _fieldType = typeof(SortedSet<T>);
        private readonly IDeepCopier<T> _elementCopier;

        /// <summary>
        /// Initializes a new instance of the <see cref="SortedSetCopier{T}"/> class.
        /// </summary>
        /// <param name="elementCopier">The element copier.</param>
        public SortedSetCopier(IDeepCopier<T> elementCopier)
        {
            _elementCopier = elementCopier;
        }

        /// <inheritdoc />
        public SortedSet<T> DeepCopy(SortedSet<T> input, CopyContext context)
        {
            if (context.TryGetCopy<SortedSet<T>>(input, out var result))
            {
                return result;
            }

            if (input.GetType() as object != _fieldType as object)
            {
                return context.DeepCopy(input);
            }

            result = new SortedSet<T>(input.Comparer);
            context.RecordCopy(input, result);
            foreach (var element in input)
            {
                result.Add(_elementCopier.DeepCopy(element, context));
            }

            return result;
        }

        /// <inheritdoc />
        public void DeepCopy(SortedSet<T> input, SortedSet<T> output, CopyContext context)
        {
            foreach (var element in input)
            {
                output.Add(_elementCopier.DeepCopy(element, context));
            }
        }
    }
}
