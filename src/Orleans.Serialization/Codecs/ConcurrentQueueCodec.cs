using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for <see cref="ConcurrentQueue{T}"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    [RegisterSerializer]
    public sealed class ConcurrentQueueCodec<T> : GeneralizedReferenceTypeSurrogateCodec<ConcurrentQueue<T>, ConcurrentQueueSurrogate<T>>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ConcurrentQueueCodec{T}"/> class.
        /// </summary>
        /// <param name="surrogateSerializer">The surrogate serializer.</param>
        public ConcurrentQueueCodec(IValueSerializer<ConcurrentQueueSurrogate<T>> surrogateSerializer) : base(surrogateSerializer)
        {
        }
        
        /// <inheritdoc/>
        public override ConcurrentQueue<T> ConvertFromSurrogate(ref ConcurrentQueueSurrogate<T> surrogate) => new(surrogate.Values);

        /// <inheritdoc/>
        public override void ConvertToSurrogate(ConcurrentQueue<T> value, ref ConcurrentQueueSurrogate<T> surrogate) => surrogate.Values = new(value);
    }

    /// <summary>
    /// Surrogate type used by <see cref="ConcurrentQueueCodec{T}"/>.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    [GenerateSerializer]
    public struct ConcurrentQueueSurrogate<T>
    {
        /// <summary>
        /// Gets or sets the values.
        /// </summary>
        /// <value>The values.</value>
        [Id(0)]
        public Queue<T> Values;
    }

    /// <summary>
    /// Copier for <see cref="ConcurrentQueue{T}"/>.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    [RegisterCopier]
    public sealed class ConcurrentQueueCopier<T> : IDeepCopier<ConcurrentQueue<T>>, IBaseCopier<ConcurrentQueue<T>>
    {
        private readonly Type _fieldType = typeof(ConcurrentQueue<T>);
        private readonly IDeepCopier<T> _copier;

        /// <summary>
        /// Initializes a new instance of the <see cref="ConcurrentQueueCopier{T}"/> class.
        /// </summary>
        /// <param name="valueCopier">The value copier.</param>
        public ConcurrentQueueCopier(IDeepCopier<T> valueCopier)
        {
            _copier = valueCopier;
        }

        /// <inheritdoc/>
        public ConcurrentQueue<T> DeepCopy(ConcurrentQueue<T> input, CopyContext context)
        {
            if (context.TryGetCopy<ConcurrentQueue<T>>(input, out var result))
            {
                return result;
            }

            if (input.GetType() as object != _fieldType as object)
            {
                return context.DeepCopy(input);
            }

            result = new ConcurrentQueue<T>();
            context.RecordCopy(input, result);
            foreach (var item in input)
            {
                result.Enqueue(_copier.DeepCopy(item, context));
            }

            return result;
        }

        /// <inheritdoc/>
        public void DeepCopy(ConcurrentQueue<T> input, ConcurrentQueue<T> output, CopyContext context)
        {
            foreach (var item in input)
            {
                output.Enqueue(_copier.DeepCopy(item, context));
            }
        }
    }
}