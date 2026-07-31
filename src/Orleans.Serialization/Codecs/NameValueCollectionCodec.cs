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
            var result = new NameValueCollection(surrogate.Values?.Count ?? 0);
            if (surrogate.Values is { } values)
            {
                foreach (var value in values)
                {
                    result.Add(value.Key, value.Value);
                }
            }

            if (surrogate.HasNullKey)
            {
                result.Add(null, surrogate.NullKeyValue);
            }

            return result;
        }

        /// <inheritdoc/>
        public override void ConvertToSurrogate(NameValueCollection value, ref NameValueCollectionSurrogate surrogate)
        {
            var result = new Dictionary<string, string?>(value.Count);
            surrogate.HasNullKey = false;
            surrogate.NullKeyValue = null;
            for (var i = 0; i < value.Count; i++)
            {
                var key = value.GetKey(i);
                if (key is null)
                {
                    surrogate.HasNullKey = true;
                    surrogate.NullKeyValue = value.Get(i);
                }
                else
                {
                    result.Add(key, value.Get(i));
                }
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
        public Dictionary<string, string?>? Values;

        /// <summary>
        /// Gets or sets a value indicating whether the collection contains a null key.
        /// </summary>
        [Id(1)]
        public bool HasNullKey;

        /// <summary>
        /// Gets or sets the value associated with the null key.
        /// </summary>
        [Id(2)]
        public string? NullKeyValue;
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
                return result!;
            }

            if (input.GetType() != typeof(NameValueCollection))
            {
                return context.DeepCopy(input)!;
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
