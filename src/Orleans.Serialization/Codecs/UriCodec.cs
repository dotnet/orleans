using System;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for <see cref="Uri"/>.
    /// </summary>
    [RegisterSerializer]
    public sealed class UriCodec : GeneralizedReferenceTypeSurrogateCodec<Uri, UriSurrogate>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="UriCodec"/> class.
        /// </summary>
        /// <param name="surrogateSerializer">The surrogate serializer.</param>
        public UriCodec(IValueSerializer<UriSurrogate> surrogateSerializer) : base(surrogateSerializer)
        {
        }

        /// <inheritdoc />
        public override Uri ConvertFromSurrogate(ref UriSurrogate surrogate) => new(surrogate.Value, UriKind.RelativeOrAbsolute);

        /// <inheritdoc />
        public override void ConvertToSurrogate(Uri value, ref UriSurrogate surrogate) => surrogate.Value = value.OriginalString;
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
        [Id(0)]
        public string Value;
    }

    [RegisterCopier]
    internal sealed class UriCopier : ShallowCopier<Uri>, IDerivedTypeCopier { }
}