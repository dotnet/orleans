using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for <see cref="ImmutableQueue{T}"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    [RegisterSerializer]
    public sealed class ImmutableQueueCodec<T> : GeneralizedReferenceTypeSurrogateCodec<ImmutableQueue<T>, ImmutableQueueSurrogate<T>>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ImmutableQueueCodec{T}"/> class.
        /// </summary>
        /// <param name="surrogateSerializer">The surrogate serializer.</param>
        public ImmutableQueueCodec(IValueSerializer<ImmutableQueueSurrogate<T>> surrogateSerializer) : base(surrogateSerializer)
        {
        }

        /// <inheritdoc/>
        public override ImmutableQueue<T> ConvertFromSurrogate(ref ImmutableQueueSurrogate<T> surrogate) => ImmutableQueue.CreateRange(surrogate.Values);

        /// <inheritdoc/>
        public override void ConvertToSurrogate(ImmutableQueue<T> value, ref ImmutableQueueSurrogate<T> surrogate) => surrogate.Values = new(value);
    }

    /// <summary>
    /// Surrogate type used by <see cref="ImmutableListCodec{T}"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    [GenerateSerializer]
    public struct ImmutableQueueSurrogate<T>
    {
        /// <summary>
        /// Gets or sets the values.
        /// </summary>
        /// <value>The values.</value>
        [Id(1)]
        public List<T> Values;
    }

    /// <summary>
    /// Copier for <see cref="ImmutableQueue{T}"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    [RegisterCopier]
    public sealed class ImmutableQueueCopier<T> : IDeepCopier<ImmutableQueue<T>>
    {
        private readonly IDeepCopier<T> _copier;

        public ImmutableQueueCopier(IDeepCopier<T> copier) => _copier = copier;

        /// <inheritdoc/>
        public ImmutableQueue<T> DeepCopy(ImmutableQueue<T> input, CopyContext context)
        {
            if (context.TryGetCopy<ImmutableQueue<T>>(input, out var result))
                return result;

            if (input.IsEmpty)
                return input;

            var items = new List<T>();
            foreach (var item in input)
                items.Add(_copier.DeepCopy(item, context));

            return ImmutableQueue.CreateRange(items);
        }
    }
}