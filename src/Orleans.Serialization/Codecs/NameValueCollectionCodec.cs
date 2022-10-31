using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;
using System.Collections.Generic;
using System.Collections.Specialized;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for <see cref="NameValueCollection"/>.
    /// </summary>
    [RegisterSerializer]
    public sealed class NameValueCollectionCodec : GeneralizedReferenceTypeSurrogateCodec<NameValueCollection, NameValueCollectionSurrogate>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="NameValueCollectionCodec"/> class.
        /// </summary>
        /// <param name="surrogateSerializer">The surrogate serializer.</param>
        public NameValueCollectionCodec(IValueSerializer<NameValueCollectionSurrogate> surrogateSerializer) : base(surrogateSerializer)
        {
        }

        /// <inheritdoc/>
        public override NameValueCollection ConvertFromSurrogate(ref NameValueCollectionSurrogate surrogate)
        {
            var result = new NameValueCollection(surrogate.Values.Count);
            foreach (var value in surrogate.Values)
            {
                result.Add(value.Key, value.Value);
            }

            return result;
        }

        /// <inheritdoc/>
        public override void ConvertToSurrogate(NameValueCollection value, ref NameValueCollectionSurrogate surrogate)
        {
            var result = new Dictionary<string, string>(value.Count);
            for (var i = 0; i < value.Count; i++)
            {
                result.Add(value.GetKey(i), value.Get(i));
            }

            surrogate.Values = result;
        }
    }

    /// <summary>
    /// Surrogate type used by <see cref="NameValueCollectionCodec"/>.
    /// </summary>
    [GenerateSerializer]
    public struct NameValueCollectionSurrogate
    {
        /// <summary>
        /// Gets or sets the values.
        /// </summary>
        /// <value>The values.</value>
        [Id(0)]
        public Dictionary<string, string> Values;
    }

    /// <summary>
    /// Copier for <see cref="NameValueCollection"/>.
    /// </summary>
    [RegisterCopier]
    public sealed class NameValueCollectionCopier : IDeepCopier<NameValueCollection>
    {
        /// <inheritdoc/>
        public NameValueCollection DeepCopy(NameValueCollection input, CopyContext context)
        {
            if (context.TryGetCopy<NameValueCollection>(input, out var result))
            {
                return result;
            }

            if (input.GetType() != typeof(NameValueCollection))
            {
                return context.DeepCopy(input);
            }

            result = new NameValueCollection(input.Count);
            context.RecordCopy(input, result);
            for (var i = 0; i < input.Count; i++)
            {
                result.Add(input.GetKey(i), input.Get(i));
            }

            return result;
        }
    }
}
