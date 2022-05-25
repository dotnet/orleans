using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;
using System.Collections;
using System.Collections.Generic;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for <see cref="ArrayList"/>.
    /// </summary>
    [RegisterSerializer]
    public sealed class ArrayListCodec : GeneralizedReferenceTypeSurrogateCodec<ArrayList, ArrayListSurrogate>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ArrayListCodec"/> class.
        /// </summary>
        /// <param name="surrogateSerializer">The surrogate serializer.</param>
        public ArrayListCodec(IValueSerializer<ArrayListSurrogate> surrogateSerializer) : base(surrogateSerializer)
        {
        }

        /// <inheritdoc/>
        public override ArrayList ConvertFromSurrogate(ref ArrayListSurrogate surrogate) => surrogate.Values switch
        {
            null => default,
            object => new ArrayList(surrogate.Values)
        };

        /// <inheritdoc/>
        public override void ConvertToSurrogate(ArrayList value, ref ArrayListSurrogate surrogate)
        {
            if (value is null)
            {
                surrogate = default;
            }
            else
            {
                var result = new List<object>(value.Count);
                foreach (var item in value)
                {
                    result.Add(item);
                }

                surrogate = new ArrayListSurrogate
                {
                    Values = result
                };
            }
        }
    }

    /// <summary>
    /// Surrogate type used by <see cref="ArrayListCodec"/>.
    /// </summary>
    [GenerateSerializer]
    public struct ArrayListSurrogate
    {
        /// <summary>
        /// Gets or sets the values.
        /// </summary>
        /// <value>The values.</value>
        [Id(1)]
        public List<object> Values { get; set; }
    }

    /// <summary>
    /// Copier for <see cref="ArrayList"/>.
    /// </summary>
    [RegisterCopier]
    public sealed class ArrayListCopier : IDeepCopier<ArrayList>, IBaseCopier<ArrayList>
    {
        private readonly IDeepCopier<object> _copier;

        /// <summary>
        /// Initializes a new instance of the <see cref="ArrayListCopier"/> class.
        /// </summary>
        /// <param name="copier">The copier.</param>
        public ArrayListCopier(IDeepCopier<object> copier)
        {
            _copier = copier;
        }

        /// <inheritdoc/>
        public ArrayList DeepCopy(ArrayList input, CopyContext context)
        {
            if (context.TryGetCopy<ArrayList>(input, out var result))
            {
                return result;
            }

            if (input.GetType() != typeof(ArrayList))
            {
                return context.DeepCopy(input);
            }

            result = new ArrayList(input.Count);
            context.RecordCopy(input, result);
            foreach (var item in input)
            {
                result.Add(_copier.DeepCopy(item, context));
            }

            return result;
        }

        /// <inheritdoc/>
        public void DeepCopy(ArrayList input, ArrayList output, CopyContext context)
        {
            foreach (var item in input)
            {
                output.Add(_copier.DeepCopy(item, context));
            }
        }
    }
}
