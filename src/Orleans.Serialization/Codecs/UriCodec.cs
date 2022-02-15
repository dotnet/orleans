using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;
using System;
using System.ComponentModel;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for <see cref="Uri"/>.
    /// </summary>
    [RegisterSerializer]
    public sealed class UriCodec : GeneralizedReferenceTypeSurrogateCodec<Uri, UriSurrogate>
    {
        private readonly TypeConverter _uriConverter = TypeDescriptor.GetConverter(typeof(Uri));

        /// <summary>
        /// Initializes a new instance of the <see cref="UriCodec"/> class.
        /// </summary>
        /// <param name="surrogateSerializer">The surrogate serializer.</param>
        public UriCodec(IValueSerializer<UriSurrogate> surrogateSerializer) : base(surrogateSerializer)
        {
        }

        /// <inheritdoc />
        public override Uri ConvertFromSurrogate(ref UriSurrogate surrogate) => surrogate.Value switch
        {
            null => null,
            _ => (Uri)_uriConverter.ConvertFromInvariantString(surrogate.Value)
        };

        /// <inheritdoc />
        public override void ConvertToSurrogate(Uri value, ref UriSurrogate surrogate) => surrogate = value switch
        {
            null => default,
            _ => new UriSurrogate
            {
                Value = _uriConverter.ConvertToInvariantString(value)
            },
        };
    }

    /// <summary>
    /// Surrogate type for <see cref="UriCodec"/>.
    /// </summary>
    [GenerateSerializer]
    public struct UriSurrogate
    {
        /// <summary>
        /// Gets or sets the value.
        /// </summary>
        /// <value>The value.</value>
        [Id(1)]
        public string Value { get; set; }
    }

    /// <summary>
    /// Copier for <see cref="Uri"/>.
    /// </summary>
    [RegisterCopier]
    public sealed class UriCopier : IDeepCopier<Uri>, IGeneralizedCopier
    {
        /// <inheritdoc />
        public Uri DeepCopy(Uri input, CopyContext context) => input;

        /// <inheritdoc />
        public object DeepCopy(object input, CopyContext context) => input;

        /// <inheritdoc />
        public bool IsSupportedType(Type type) => typeof(Uri).IsAssignableFrom(type);
    }
}